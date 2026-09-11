using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>The edit-while-generating race flagged in PR #86 review: GetEditableDraftAsync/
/// ApplyEditAndRegenerateAsync must reject a job the background worker hasn't finished with yet, since
/// DataJson is already populated once fetch completes (status Generating) — well before the worker's own
/// ProcessAsync run finishes writing ReportStoragePath/DataJson/Status. Without the Completed-only guard,
/// an edit submitted during that window regenerates concurrently with the worker and races it for those
/// same fields.</summary>
public class PreLoginReportJobServiceTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<long> SeedJobAsync(PreLoginReportJobStatus status, string? dataJson)
    {
        await using var db = CreateContext();
        var job = new PreLoginReportJob
        {
            BatchId = Guid.NewGuid(), Cin = "U12345DL2025PTC123456", Format = nameof(PreLoginReportFormat.Sbi),
            Status = status, DataJson = dataJson, CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginReportJobs.Add(job);
        await db.SaveChangesAsync();
        return job.PreLoginReportJobId;
    }

    private static PreLoginReportJobService CreateService(AppDbContext db) =>
        new(db, new PreLoginReportQueue(), new PreLoginReportService(NeverCalledClient(), new TestEnvironment()), new TestEnvironment());

    // A GetEditableDraftAsync/ApplyEditAndRegenerateAsync call rejected for a non-Completed job must never
    // reach PreLoginReportService/InstaFinancialsClient at all — this client throws if it's ever invoked,
    // turning "the guard was bypassed" into a loud test failure instead of a silent network call.
    private static InstaFinancialsClient NeverCalledClient() => new(
        new HttpClient(new ThrowingHandler()), Options.Create(new InstaFinancialsOptions { ApiKey = "unused" }));

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The report data client must not be called for a job the Completed-only guard should have rejected.");
    }

    private const string SamplePayload = """
    { "ReportData": { "companyData": { "company":"Example Private Limited" }, "indexChargesData":[], "directorData":[] } }
    """;

    [Theory]
    [InlineData(PreLoginReportJobStatus.Queued)]
    [InlineData(PreLoginReportJobStatus.Fetching)]
    [InlineData(PreLoginReportJobStatus.Generating)]
    public async Task GetEditableDraftAsync_rejects_a_job_the_worker_has_not_finished_with(PreLoginReportJobStatus status)
    {
        // Fetching/Generating already have DataJson populated in the real pipeline (ProcessAsync writes it
        // right after fetch, before generation) — seeding it here proves the rejection is a status check,
        // not just a missing-data check.
        var jobId = await SeedJobAsync(status, SamplePayload);
        await using var db = CreateContext();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => service.GetEditableDraftAsync(jobId, CancellationToken.None));
        Assert.Contains("still being generated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PreLoginReportJobStatus.Queued)]
    [InlineData(PreLoginReportJobStatus.Fetching)]
    [InlineData(PreLoginReportJobStatus.Generating)]
    public async Task ApplyEditAndRegenerateAsync_rejects_a_job_the_worker_has_not_finished_with(PreLoginReportJobStatus status)
    {
        var jobId = await SeedJobAsync(status, SamplePayload);
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = new PreLoginReportDraftViewModel { JobId = jobId, Cin = "U12345DL2025PTC123456", Format = PreLoginReportFormat.Sbi, Company = new EditableCompanyViewModel { Name = "Example Private Limited" } };

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => service.ApplyEditAndRegenerateAsync(jobId, draft, CancellationToken.None));
        Assert.Contains("still being generated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetEditableDraftAsync_allows_a_completed_job()
    {
        // Unlike SamplePayload (the raw InstaFinancials wire format used by InstaFinancialsClient), a real
        // job's DataJson is `JsonSerializer.Serialize(InstaReportData)` — the already-parsed internal
        // shape ProcessAsync writes right after fetch. Seed that shape here so GetEditableDraftAsync's own
        // Deserialize<InstaReportData> call succeeds, the same way it would against real stored data.
        var data = new InstaReportData(
            new InstaCompany("Example Private Limited", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], []);
        var jobId = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data));
        await using var db = CreateContext();
        var service = CreateService(db);

        var draft = await service.GetEditableDraftAsync(jobId, CancellationToken.None);

        Assert.Equal(jobId, draft.JobId);
        Assert.Equal("Example Private Limited", draft.Company.Name);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MCAROC_Analysis"));
        public string ApplicationName { get; set; } = "MCAROC_Analysis.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Root;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(Root);
    }
}
