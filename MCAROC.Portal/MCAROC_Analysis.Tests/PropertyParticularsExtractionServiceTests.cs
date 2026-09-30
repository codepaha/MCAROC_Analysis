using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.PropertyParticulars;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Scheduling and processing of the Gemini property-particulars extraction against real SQL Server, with a
/// scripted fake model: one row per distinct text (not per event), idempotent re-scheduling, inert without Vertex
/// configuration, grounded publication, retry then terminal failure, and the lease fence.</summary>
public sealed class PropertyParticularsExtractionServiceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class ScriptedClient(Func<string, PropertyParticularsAiCallResult> respond) : IPropertyParticularsAiClient
    {
        public int Calls;
        public Task<PropertyParticularsAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(prompt));
        }
    }

    private static readonly IConfiguration VertexConfigured = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["GoogleCloud:ProjectId"] = "p", ["GoogleCloud:CredentialsPath"] = "c.json" })
        .Build();

    private static PropertyParticularsExtractionService Service(AppDbContext db, IPropertyParticularsAiClient? client = null,
        IConfiguration? configuration = null, bool enabled = true, PropertyParticularsExtractionQueue? queue = null)
    {
        var services = new ServiceCollection();
        if (client is not null) services.AddSingleton(client);
        return new PropertyParticularsExtractionService(db, queue ?? new PropertyParticularsExtractionQueue(),
            Options.Create(new PropertyParticularsExtractionOptions { Enabled = enabled, MaxAttempts = 2 }),
            configuration ?? VertexConfigured, services.BuildServiceProvider(), NullLogger<PropertyParticularsExtractionService>.Instance);
    }

    private static string Unique(string text) => $"{text} (ref {Guid.NewGuid():N})";

    [Fact]
    public async Task Scheduling_creates_one_row_per_distinct_text_and_is_idempotent()
    {
        var mortgage = Unique("All that premises bearing Unit No. 5c on the 5th Floor in the building known as Godrej One");
        var hypothecation = Unique("All current assets of the Company including book debts and receivables");
        var requestId = await SeedRequestAsync(
            (mortgage, "Immovable"), (mortgage.Replace(" on the", "\non  the"), "Immovable"), (hypothecation, "Movable"), ("-", null), (null, null));

        await using var db = CreateContext();
        var queue = new PropertyParticularsExtractionQueue();
        Assert.Equal(2, await Service(db, queue: queue).ScheduleForRequestAsync(requestId, CancellationToken.None));
        Assert.Equal(0, await Service(db, queue: queue).ScheduleForRequestAsync(requestId, CancellationToken.None));

        var hashes = new[] { PropertyParticularsAi.HashOf(mortgage, "Immovable"), PropertyParticularsAi.HashOf(hypothecation, "Movable") };
        var rows = await db.PropertyParticularsExtractions.AsNoTracking().Where(x => hashes.Contains(x.TextHash)).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(PropertyParticularsExtractionStatus.Pending, r.Status));
    }

    [Fact]
    public async Task Scheduling_is_inert_when_disabled_or_vertex_is_not_configured()
    {
        var requestId = await SeedRequestAsync((Unique("Plot No. 70 situated at Industrial Area, Chandigarh"), "Immovable"));
        await using var db = CreateContext();

        Assert.False(Service(db, enabled: false).IsActive);
        Assert.Equal(0, await Service(db, enabled: false).ScheduleForRequestAsync(requestId, CancellationToken.None));
        var unconfigured = new ConfigurationBuilder().Build();
        Assert.False(Service(db, configuration: unconfigured).IsActive);
        Assert.Equal(0, await Service(db, configuration: unconfigured).ScheduleForRequestAsync(requestId, CancellationToken.None));
    }

    [Fact]
    public async Task Processing_publishes_only_grounded_values_and_records_what_was_dropped()
    {
        var text = Unique("All that premises bearing Unit No. 5c on the 5th Floor in the building known as Godrej One situated at Vikhroli, Mumbai");
        var requestId = await SeedRequestAsync((text, "Immovable"));
        await using var db = CreateContext();
        await Service(db).ScheduleForRequestAsync(requestId, CancellationToken.None);
        var id = await IdOf(db, text, "Immovable");
        var client = new ScriptedClient(_ => new(true, """
            {"properties":[{"assetClass":"Immovable","kind":"Premises","unitNumber":"5c","floor":"5th","building":"Godrej One",
              "localities":["Vikhroli"],"city":"Mumbai","state":"Maharashtra","areas":[],"surveyNumbers":[]}]}
            """, null));

        await Service(db, client).ProcessAsync(id, CancellationToken.None);

        var row = await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == id);
        Assert.Equal(PropertyParticularsExtractionStatus.Completed, row.Status);
        Assert.Null(row.LeaseToken);
        var result = PropertyParticularsAi.Deserialize(row.ExtractionJson)!;
        var property = Assert.Single(result.Properties);
        Assert.Equal("5C", property.UnitNumber);
        Assert.Equal("Mumbai", property.City);
        Assert.Null(property.State); // "Maharashtra" is not in the text
        Assert.Contains("Maharashtra", row.RejectedFieldsJson);

        // What the charge drawer and dossier then read.
        var ev = await db.RocChargeEvents.AsNoTracking().FirstAsync(e => e.RequestId == requestId);
        var loaded = await PropertyParticularsExtractionService.LoadCompletedAsync(db, [ev], CancellationToken.None);
        Assert.Equal(PropertyReadingSource.Ai, PropertyReading.For(ev.PropertyParticulars, ev.PropertyType, loaded).Source);
    }

    [Fact]
    public async Task A_failing_call_is_retried_then_marked_failed_and_never_published()
    {
        var text = Unique("Hypothecation of all current assets and book debts");
        var requestId = await SeedRequestAsync((text, "Movable"));
        await using var db = CreateContext();
        await Service(db).ScheduleForRequestAsync(requestId, CancellationToken.None);
        var id = await IdOf(db, text, "Movable");
        var client = new ScriptedClient(_ => new(false, string.Empty, "Vertex AI call timed out after 60s."));

        await Service(db, client).ProcessAsync(id, CancellationToken.None);
        var afterFirst = await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == id);
        Assert.Equal(PropertyParticularsExtractionStatus.Pending, afterFirst.Status);
        Assert.NotNull(afterFirst.NextAttemptUtc);

        // Not due yet: a second wake-up before NextAttemptUtc is a no-op.
        await Service(db, client).ProcessAsync(id, CancellationToken.None);
        Assert.Equal(1, client.Calls);

        await db.PropertyParticularsExtractions.Where(x => x.PropertyParticularsExtractionId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptUtc, DateTime.UtcNow.AddSeconds(-1)));
        await Service(db, client).ProcessAsync(id, CancellationToken.None);

        var final = await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == id);
        Assert.Equal(PropertyParticularsExtractionStatus.Failed, final.Status); // MaxAttempts = 2
        Assert.Equal(2, final.AttemptCount);
        Assert.Null(final.ExtractionJson);
        Assert.Contains("timed out", final.FailureReason);
    }

    [Fact]
    public async Task A_row_already_claimed_by_another_worker_is_not_processed_twice()
    {
        var text = Unique("Land admeasuring 900.3 square metres at Plot No.75A, Juhu");
        var requestId = await SeedRequestAsync((text, "Immovable"));
        await using var db = CreateContext();
        await Service(db).ScheduleForRequestAsync(requestId, CancellationToken.None);
        var id = await IdOf(db, text, "Immovable");
        await db.PropertyParticularsExtractions.Where(x => x.PropertyParticularsExtractionId == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, PropertyParticularsExtractionStatus.InProgress)
            .SetProperty(x => x.LeaseToken, Guid.NewGuid())
            .SetProperty(x => x.LeaseExpiresUtc, DateTime.UtcNow.AddMinutes(5)));
        var client = new ScriptedClient(_ => throw new InvalidOperationException("must not be called"));

        await Service(db, client).ProcessAsync(id, CancellationToken.None);

        Assert.Equal(0, client.Calls);
        Assert.Equal(PropertyParticularsExtractionStatus.InProgress,
            (await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == id)).Status);
    }

    [Fact]
    public async Task Recovery_returns_an_abandoned_claim_to_pending_and_wakes_it()
    {
        var text = Unique("All that piece of land bearing Survey No. 12 situated at Village Wagholi");
        var requestId = await SeedRequestAsync((text, "Immovable"));
        await using var db = CreateContext();
        await Service(db).ScheduleForRequestAsync(requestId, CancellationToken.None);
        var id = await IdOf(db, text, "Immovable");
        await db.PropertyParticularsExtractions.Where(x => x.PropertyParticularsExtractionId == id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, PropertyParticularsExtractionStatus.InProgress)
            .SetProperty(x => x.LeaseToken, Guid.NewGuid())
            .SetProperty(x => x.LeaseExpiresUtc, DateTime.UtcNow.AddMinutes(-1)));
        var queue = new PropertyParticularsExtractionQueue();

        await Service(db, queue: queue).RecoverAsync(CancellationToken.None);

        Assert.Equal(PropertyParticularsExtractionStatus.Pending,
            (await db.PropertyParticularsExtractions.AsNoTracking().SingleAsync(x => x.PropertyParticularsExtractionId == id)).Status);
        var woken = new List<long>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await foreach (var x in queue.ReadAllAsync(cts.Token)) { woken.Add(x); if (x == id) break; } }
        catch (OperationCanceledException) { }
        Assert.Contains(id, woken);
    }

    private static async Task<long> IdOf(AppDbContext db, string text, string type)
    {
        var hash = PropertyParticularsAi.HashOf(text, type);
        return await db.PropertyParticularsExtractions.Where(x => x.TextHash == hash).Select(x => x.PropertyParticularsExtractionId).SingleAsync();
    }

    private static async Task<long> SeedRequestAsync(params (string? Particulars, string? Type)[] events)
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "PPX" + Guid.NewGuid().ToString("N")[..7], ClientName = "Property Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Property Test Co", RequestNumber = $"PPX-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(run);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = run.IngestionRunId;

        var charge = new RocCharge { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, RocChargeNumber = "C1", ChargeStatus = "Open" };
        db.RocCharges.Add(charge);
        await db.SaveChangesAsync();
        var serial = 1;
        foreach (var (particulars, type) in events)
            db.RocChargeEvents.Add(new RocChargeEvent
            {
                RocChargeId = charge.ChargeId, RequestId = request.RequestId, IngestionRunId = run.IngestionRunId,
                SerialNumber = $"1.{serial++}", EventType = ChargeEventType.Modification, HolderNameRaw = "Bank", HolderNameNormalized = "BANK",
                PropertyParticulars = particulars, PropertyType = type
            });
        await db.SaveChangesAsync();
        return request.RequestId;
    }
}
