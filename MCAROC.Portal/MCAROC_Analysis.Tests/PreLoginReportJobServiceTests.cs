using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Two concerns: (1) the edit-while-generating race flagged in PR #86 review — GetEditableDraftAsync/
/// ApplyEditAndRegenerateAsync must reject a job the background worker hasn't finished with yet, since
/// DataJson is already populated once fetch completes (status Generating) — well before the worker's own
/// ProcessAsync run finishes writing ReportStoragePath/DataJson/Status. Without the Completed-only guard,
/// an edit submitted during that window regenerates concurrently with the worker and races it for those
/// same fields. (2) The #47 ownership gap — this pipeline has no login, so every externally-reachable
/// lookup must be scoped to the job's own BatchId Guid; a correct id under the WRONG batch must be
/// rejected identically to a nonexistent id, everywhere (GetEditableDraftAsync/ApplyEditAndRegenerateAsync/
/// RerunAsync/FindInBatchAsync/HistoryAsync).</summary>
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

    private static async Task<(long JobId, Guid BatchId)> SeedJobAsync(PreLoginReportJobStatus status, string? dataJson, string cin = "U12345DL2025PTC123456")
    {
        await using var db = CreateContext();
        var job = new PreLoginReportJob
        {
            BatchId = Guid.NewGuid(), Cin = cin, Format = nameof(PreLoginReportFormat.Sbi),
            Status = status, DataJson = dataJson, CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginReportJobs.Add(job);
        await db.SaveChangesAsync();
        return (job.PreLoginReportJobId, job.BatchId);
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
        var (jobId, batchId) = await SeedJobAsync(status, SamplePayload);
        await using var db = CreateContext();
        var service = CreateService(db);

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => service.GetEditableDraftAsync(batchId, jobId, CancellationToken.None));
        Assert.Contains("still being generated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PreLoginReportJobStatus.Queued)]
    [InlineData(PreLoginReportJobStatus.Fetching)]
    [InlineData(PreLoginReportJobStatus.Generating)]
    public async Task ApplyEditAndRegenerateAsync_rejects_a_job_the_worker_has_not_finished_with(PreLoginReportJobStatus status)
    {
        var (jobId, batchId) = await SeedJobAsync(status, SamplePayload);
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = new PreLoginReportDraftViewModel { JobId = jobId, BatchId = batchId, Cin = "U12345DL2025PTC123456", Format = PreLoginReportFormat.Sbi, Company = new EditableCompanyViewModel { Name = "Example Private Limited" } };

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => service.ApplyEditAndRegenerateAsync(batchId, jobId, draft, CancellationToken.None));
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
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data));
        await using var db = CreateContext();
        var service = CreateService(db);

        var draft = await service.GetEditableDraftAsync(batchId, jobId, CancellationToken.None);

        Assert.Equal(jobId, draft.JobId);
        Assert.Equal(batchId, draft.BatchId);
        Assert.Equal("Example Private Limited", draft.Company.Name);
    }

    // ── #47: ownership binding — the batch Guid is the only access credential this pipeline has ──────

    [Fact]
    public async Task GetEditableDraftAsync_rejects_the_right_id_under_the_wrong_batch()
    {
        var data = new InstaReportData(new InstaCompany("Example Private Limited", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"), [], []);
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data));
        await using var db = CreateContext();
        var service = CreateService(db);

        // A different, real (but unrelated) batch Guid — never the seeded job's own.
        var exception = await Assert.ThrowsAsync<PreLoginReportException>(
            () => service.GetEditableDraftAsync(Guid.NewGuid(), jobId, CancellationToken.None));
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyEditAndRegenerateAsync_rejects_the_right_id_under_the_wrong_batch()
    {
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.Completed, null);
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = new PreLoginReportDraftViewModel { JobId = jobId, Cin = "U12345DL2025PTC123456", Format = PreLoginReportFormat.Sbi, Company = new EditableCompanyViewModel { Name = "X" } };

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(
            () => service.ApplyEditAndRegenerateAsync(Guid.NewGuid(), jobId, draft, CancellationToken.None));
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RerunAsync_rejects_the_right_id_under_the_wrong_batch_and_never_touches_the_job()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Failed, null);
        await using var db = CreateContext();
        var service = CreateService(db);

        await Assert.ThrowsAsync<PreLoginReportException>(() => service.RerunAsync(Guid.NewGuid(), jobId, CancellationToken.None));

        // The job must be untouched by the rejected call — still Failed, not silently re-queued.
        await using var verifyDb = CreateContext();
        var job = await verifyDb.PreLoginReportJobs.SingleAsync(x => x.PreLoginReportJobId == jobId);
        Assert.Equal(PreLoginReportJobStatus.Failed, job.Status);
        Assert.Equal(batchId, job.BatchId);
    }

    [Fact]
    public async Task FindInBatchAsync_returns_null_identically_for_a_nonexistent_id_and_a_wrong_batch()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, null);
        await using var db = CreateContext();
        var service = CreateService(db);

        // Right id, wrong batch — must not leak that this id exists under some other batch.
        Assert.Null(await service.FindInBatchAsync(Guid.NewGuid(), jobId, CancellationToken.None));
        // Right batch, wrong id.
        Assert.Null(await service.FindInBatchAsync(batchId, jobId + 1, CancellationToken.None));
        // The real pair still resolves.
        Assert.NotNull(await service.FindInBatchAsync(batchId, jobId, CancellationToken.None));
    }

    [Fact]
    public async Task HistoryAsync_returns_only_the_requested_batchs_jobs()
    {
        var (jobA, batchA) = await SeedJobAsync(PreLoginReportJobStatus.Completed, null, "U11111DL2025PTC111111");
        var (jobB, batchB) = await SeedJobAsync(PreLoginReportJobStatus.Completed, null, "U22222DL2025PTC222222");
        await using var db = CreateContext();
        var service = CreateService(db);

        var historyA = await service.HistoryAsync(batchA, CancellationToken.None);
        Assert.Single(historyA);
        Assert.Equal(jobA, historyA[0].PreLoginReportJobId);

        var historyB = await service.HistoryAsync(batchB, CancellationToken.None);
        Assert.Single(historyB);
        Assert.Equal(jobB, historyB[0].PreLoginReportJobId);

        // An unrelated, never-seeded batch Guid sees nothing — not an error, not everyone else's jobs.
        Assert.Empty(await service.HistoryAsync(Guid.NewGuid(), CancellationToken.None));
    }

    // ── #221: litigation file upload — identity must key off Company.IsPartnership, never off whether
    // LegalCases happens to be populated, since Company/LLP jobs can now carry uploaded litigation too. ──

    static PreLoginReportJobServiceTests() => Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

    private const string CsvHeader = "Court,Case_Number,Role,Case_Type,Case_Status,Case_Stage,Filing_Date,Case_Year,Court_Forum,State,District,Petitioners,Petitioner_Advocates,Respondents,Respondent_Advocates,LDOH_NDOH,Act,Cnr_Number,Bench,Side";
    private const string CsvRow = "district,1/2020,,CS,PENDING,NA,01-01-2020,2020,Civil Court Rohtak,Haryana,Rohtak,A Ltd,,B Ltd,,15-09-2026,CPC,CNR1,,other";

    private static IFormFile MakeCasesFile(string fileName = "cases.csv")
    {
        var bytes = Encoding.UTF8.GetBytes(CsvHeader + "\r\n" + CsvRow);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "legalCasesFile", fileName) { Headers = new Microsoft.AspNetCore.Http.HeaderDictionary(), ContentType = "text/csv" };
    }

    private async Task CleanupGeneratedReportAsync(long jobId)
    {
        await using var db = CreateContext();
        var job = await db.PreLoginReportJobs.FindAsync(jobId);
        if (job?.ReportStoragePath is { } path && File.Exists(path)) File.Delete(path);
    }

    [Fact]
    public async Task ApplyEditAndRegenerateAsync_keeps_job_cin_for_a_company_job_even_once_legal_cases_are_attached()
    {
        // Regression test: the identifier used to key off `data.LegalCases is null`, which broke the moment
        // a non-partnership (Company/LLP) job could also carry LegalCases (#221) — it must key off the
        // independent Company.IsPartnership flag instead.
        var data = new InstaReportData(
            new InstaCompany("Example Private Limited", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", IsPartnership: false),
            [], []);
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data), "U12345DL2025PTC123456");
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = await service.GetEditableDraftAsync(batchId, jobId, CancellationToken.None);

        try
        {
            await service.ApplyEditAndRegenerateAsync(batchId, jobId, draft, CancellationToken.None, MakeCasesFile());

            await using var verifyDb = CreateContext();
            var job = await verifyDb.PreLoginReportJobs.SingleAsync(x => x.PreLoginReportJobId == jobId);
            Assert.Equal("U12345DL2025PTC123456", job.Cin);
            var stored = JsonSerializer.Deserialize<InstaReportData>(job.DataJson!)!;
            Assert.False(stored.Company.IsPartnership);
            Assert.NotNull(stored.LegalCases);
            Assert.Equal(1, stored.LegalCases!.DistrictCourt);
            Assert.Single(stored.LegalCases.Cases!);
        }
        finally { await CleanupGeneratedReportAsync(jobId); }
    }

    [Fact]
    public async Task ApplyEditAndRegenerateAsync_uses_the_edited_registration_number_for_a_partnership()
    {
        var data = new InstaReportData(
            new InstaCompany("ABC Partners", "-", "REG-OLD", "Partnership", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", IsPartnership: true),
            [], [], new InstaLegalCases(0, 0, 0, 0, 0, 0, 0, 0, 0));
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data), "REG-OLD");
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = await service.GetEditableDraftAsync(batchId, jobId, CancellationToken.None);
        draft.Company.RegistrationNumber = "REG-NEW";

        try
        {
            await service.ApplyEditAndRegenerateAsync(batchId, jobId, draft, CancellationToken.None);

            await using var verifyDb = CreateContext();
            var job = await verifyDb.PreLoginReportJobs.SingleAsync(x => x.PreLoginReportJobId == jobId);
            Assert.Equal("REG-NEW", job.Cin);
        }
        finally { await CleanupGeneratedReportAsync(jobId); }
    }

    [Fact]
    public async Task ApplyEditAndRegenerateAsync_preserves_previously_attached_legal_cases_when_no_new_file_is_uploaded()
    {
        var existingCases = LegalCaseFileParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(CsvHeader + "\r\n" + CsvRow)), "cases.csv");
        var data = new InstaReportData(
            new InstaCompany("Example Private Limited", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], [], LegalCaseFileParser.ToInstaLegalCases(existingCases));
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data), "U12345DL2025PTC123456");
        await using var db = CreateContext();
        var service = CreateService(db);
        var draft = await service.GetEditableDraftAsync(batchId, jobId, CancellationToken.None);

        try
        {
            // No legalCasesFile this time — a plain edit of an unrelated field.
            await service.ApplyEditAndRegenerateAsync(batchId, jobId, draft, CancellationToken.None);

            await using var verifyDb = CreateContext();
            var job = await verifyDb.PreLoginReportJobs.SingleAsync(x => x.PreLoginReportJobId == jobId);
            var stored = JsonSerializer.Deserialize<InstaReportData>(job.DataJson!)!;
            Assert.NotNull(stored.LegalCases?.Cases);
            Assert.Single(stored.LegalCases!.Cases!);
        }
        finally { await CleanupGeneratedReportAsync(jobId); }
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
