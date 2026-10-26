using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#554: scheduling and running the reading of each order's properties — which orders are read, what costs a call, the daily limit,
/// and the litigation stage waiting for the readings.</summary>
public class OrderPropertyReadingServiceTests : IAsyncLifetime
{
    private const string PropertyOrder = """
        --- Page 1 (native) ---
        IN THE HIGH COURT OF BOMBAY
        --- Page 2 (native) ---
        The plaintiff booked flat no.1804 having super built up area of 3282 sq. ft. in the project Godrej Alpine at Padavu Village, Mangalore.
        """;

    private const string ProceduralOrder = """
        --- Page 1 (native) ---
        Respondent Nos. 1 and 2 have already filed counter affidavit. List again on 14.7.2025.
        """;

    private const string Answer = """
        {"properties":[{"page":2,"sourceText":"flat no.1804 having super built up area of 3282 sq. ft. in the project Godrej Alpine at Padavu Village, Mangalore",
          "role":"Disputed","relation":"SoldByTarget","issue":"DelayedPossession","directions":[],"kind":"Premises","unitNumber":"1804",
          "project":"Godrej Alpine","areas":[],"surveyNumbers":[],"localities":[],"village":"Padavu Village","city":"Mangalore"}],"unidentified":[]}
        """;

    private sealed class FakeClient : IPropertyParticularsAiClient
    {
        public List<string> Prompts { get; } = [];
        public Task<PropertyParticularsAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
        {
            lock (Prompts) Prompts.Add(prompt);
            return Task.FromResult(new PropertyParticularsAiCallResult(true, Answer, null));
        }
    }

    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        // Readings are global work: start every test from none, so one test's rows are not another's to schedule or reuse.
        await db.LitigationOrderPropertyReadings.ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly IConfiguration Vertex = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleCloud:ProjectId"] = "p", ["GoogleCloud:CredentialsPath"] = "c" }).Build();

    private static OrderPropertyReadingService Service(AppDbContext db, FakeClient client, int maxCallsPerDay = 100) =>
        new(db, Options.Create(new OrderPropertyReadingOptions { MaxCallsPerDay = maxCallsPerDay, ScheduleBatch = 10_000 }), Vertex,
            new ServiceCollection().AddSingleton<IPropertyParticularsAiClient>(client).BuildServiceProvider(), NullLogger<OrderPropertyReadingService>.Instance);

    /// <summary>A request with one case per order text; returns the request and each order's document id.</summary>
    private static async Task<(long RequestId, List<long> Documents)> SeedAsync(AppDbContext db, params (string Text, LitigationAttributionOutcome? Outcome)[] orders)
    {
        var client = new Client { ClientCode = "OPR" + Guid.NewGuid().ToString("N")[..7], ClientName = "Order Property Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = "Godrej Properties Limited", RequestNumber = $"OPR-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow, RawResponseHash = "h" + request.RequestId };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        db.LitigationReportSnapshots.Add(new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId,
            ReportHash = job.RawResponseHash!, Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow });
        var documents = new List<long>();
        foreach (var (text, outcome) in orders)
        {
            var c = new LitigationCase { RequestId = request.RequestId, Court = "BombayHighCourt", CaseNumber = "CS " + Guid.NewGuid().ToString("N")[..6], CaseStatus = "Pending",
                PetitionersJson = "[\"Nupur Bansal\"]", RespondentsJson = "[\"Godrej Properties Ltd\"]", AttributionOutcome = outcome, FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
            db.LitigationCases.Add(c);
            await db.SaveChangesAsync();
            var o = new LitigationCaseOrder { LitigationCaseId = c.LitigationCaseId, OrderDate = "15-04-2025", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
            db.LitigationCaseOrders.Add(o);
            await db.SaveChangesAsync();
            var d = new LitigationOrderDocument { LitigationCaseOrderId = o.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded,
                RetainedUntilUtc = DateTime.UtcNow.AddDays(7), ExtractedText = text, TextExtractionStatus = FilingDocumentProcessingStatus.TextExtracted,
                ChunkingStatus = ChunkingStatus.Chunked };
            db.LitigationOrderDocuments.Add(d);
            await db.SaveChangesAsync();
            documents.Add(d.LitigationOrderDocumentId);
        }
        return (request.RequestId, documents);
    }

    private static Task<LitigationOrderPropertyReading?> ReadingOf(AppDbContext db, long documentId) =>
        db.LitigationOrderPropertyReadings.AsNoTracking().SingleOrDefaultAsync(r => r.LitigationOrderDocumentId == documentId);

    [Fact]
    public async Task An_order_with_a_property_is_read_once_and_a_procedural_or_set_aside_order_costs_nothing()
    {
        await using var db = CreateContext();
        var (requestId, docs) = await SeedAsync(db, (PropertyOrder, LitigationAttributionOutcome.Attributed), (ProceduralOrder, null),
            (PropertyOrder + "\nSet aside.", LitigationAttributionOutcome.NotTheCompany));
        var client = new FakeClient();
        var service = Service(db, client);

        var due = await service.ScheduleAsync(default, requestId);
        foreach (var id in due) await service.ProcessAsync(id, default);

        var read = (await ReadingOf(db, docs[0]))!;
        Assert.True(read.Status == LitigationOrderPropertyReadingStatus.Completed, $"{read.Status}: {read.FailureReason}");
        var p = Assert.Single(OrderPropertiesAi.Deserialize(read.ResultJson)!.Properties);
        Assert.Equal("1804", p.Property.UnitNumber);
        Assert.Equal("[2]", read.PagesSentJson);                                  // only the page that may name a property
        Assert.Equal(LitigationOrderPropertyReadingStatus.NoPropertyPages, (await ReadingOf(db, docs[1]))!.Status);
        Assert.Null(await ReadingOf(db, docs[2]));                                 // a case the request does not show is not read
        Assert.Single(client.Prompts);
        Assert.Contains("Godrej Properties Limited", client.Prompts[0]);
        Assert.Empty(await service.ScheduleAsync(default, requestId));                         // nothing left to schedule
    }

    [Fact]
    public async Task An_order_whose_input_another_order_had_takes_that_reading_without_a_call()
    {
        await using var db = CreateContext();
        var (requestId, first) = await SeedAsync(db, (PropertyOrder, LitigationAttributionOutcome.Attributed));
        var client = new FakeClient();
        var service = Service(db, client);
        foreach (var id in await service.ScheduleAsync(default, requestId)) await service.ProcessAsync(id, default);

        // The same order on another request of the same company (a refresh, a reused report): the same prompt, so the same reading.
        await using var db2 = CreateContext();
        var (requestId2, second) = await SeedAsync(db2, (PropertyOrder, LitigationAttributionOutcome.Attributed));
        var service2 = Service(db2, client);
        foreach (var id in await service2.ScheduleAsync(default, requestId2)) await service2.ProcessAsync(id, default);

        var reused = (await ReadingOf(db2, second[0]))!;
        Assert.Equal(LitigationOrderPropertyReadingStatus.Completed, reused.Status);
        Assert.Equal((await ReadingOf(db, first[0]))!.LitigationOrderPropertyReadingId, reused.ReusedFromReadingId);
        Assert.Single(client.Prompts);
    }

    [Fact]
    public async Task Past_the_daily_limit_a_reading_waits_for_the_next_IST_day()
    {
        await using var db = CreateContext();
        var (requestId, docs) = await SeedAsync(db, (PropertyOrder, LitigationAttributionOutcome.Attributed));
        var client = new FakeClient();
        var service = Service(db, client, maxCallsPerDay: 0);

        foreach (var id in await service.ScheduleAsync(default, requestId)) await service.ProcessAsync(id, default);

        var row = (await ReadingOf(db, docs[0]))!;
        Assert.Equal(LitigationOrderPropertyReadingStatus.Pending, row.Status);
        Assert.Equal(Ist.NextMidnightUtc(DateTime.UtcNow), row.NextAttemptUtc);
        Assert.Empty(client.Prompts);
        Assert.Empty(await service.DueAsync(10, default));
    }

    [Fact]
    public async Task The_litigation_stage_waits_until_the_orders_are_read()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedAsync(db, (PropertyOrder, LitigationAttributionOutcome.Attributed), (ProceduralOrder, LitigationAttributionOutcome.Attributed));
        var reader = new PipelineSnapshotReader(db, Vertex, Options.Create(new BprLitigationOptions()), Options.Create(new ReferenceToolOptions()));

        Assert.False((await reader.ReadAsync(requestId, default))!.LitigationSearch!.OrdersFullyProcessed);

        var service = Service(db, new FakeClient());
        foreach (var id in await service.ScheduleAsync(default, requestId)) await service.ProcessAsync(id, default);

        Assert.True((await reader.ReadAsync(requestId, default))!.LitigationSearch!.OrdersFullyProcessed);
    }

    [Fact]
    public async Task Without_Vertex_configured_nothing_is_scheduled_and_the_stage_does_not_wait()
    {
        await using var db = CreateContext();
        var (requestId, _) = await SeedAsync(db, (PropertyOrder, LitigationAttributionOutcome.Attributed));
        var none = new ConfigurationBuilder().Build();
        var service = new OrderPropertyReadingService(db, Options.Create(new OrderPropertyReadingOptions()), none,
            new ServiceCollection().BuildServiceProvider(), NullLogger<OrderPropertyReadingService>.Instance);

        Assert.Empty(await service.ScheduleAsync(default, requestId));
        var reader = new PipelineSnapshotReader(db, none, Options.Create(new BprLitigationOptions()), Options.Create(new ReferenceToolOptions()));
        Assert.True((await reader.ReadAsync(requestId, default))!.LitigationSearch!.OrdersFullyProcessed);
    }
}
