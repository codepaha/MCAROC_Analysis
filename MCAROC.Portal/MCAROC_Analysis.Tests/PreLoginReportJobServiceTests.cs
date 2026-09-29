using System.Text;
using System.Text.Json;
using System.Net;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
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

    [Fact]
    public async Task Preview_and_download_expose_the_completed_report_and_attached_cases_only_in_its_batch()
    {
        var caseRecord = new LegalCaseRecord("district", "Labour Court, Sangli", "-", "ECA/15/2024", "ECA", "2024",
            "Hearing", "-", "-", "Maharashtra", "Sangli", "Borrower vs claimant", "-", "Pending");
        var data = new InstaReportData(new InstaCompany("Example Private Limited", "ROC Pune", "123456", "-", "-", "-",
            "-", "-", "-", "01-01-2020", "Sangli", "-", "Unlisted", "-", "-", "Active"), [], [],
            LegalCaseFileParser.ToInstaLegalCases([caseRecord]));
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.Completed, JsonSerializer.Serialize(data));
        var reportPath = Path.Combine(Path.GetTempPath(), $"{jobId}-Example_SBI.docx");
        await File.WriteAllBytesAsync(reportPath, [1, 2, 3]);
        try
        {
            await using var db = CreateContext();
            var job = await db.PreLoginReportJobs.SingleAsync(x => x.PreLoginReportJobId == jobId);
            job.ReportStoragePath = reportPath;
            await db.SaveChangesAsync();
            var controller = new PreLoginReportsController(CreateService(db))
            { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

            var preview = Assert.IsType<ViewResult>(await controller.Preview(batchId, jobId, CancellationToken.None));
            var visible = Assert.IsType<InstaReportData>(preview.Model);
            Assert.Equal("ECA/15/2024", Assert.Single(visible.LegalCases!.Cases!).CaseNo);
            Assert.Equal(1, visible.LegalCases.DistrictCourt);
            Assert.True((bool)controller.ViewBag.DownloadAvailable);
            Assert.IsType<NotFoundResult>(await controller.Preview(Guid.NewGuid(), jobId, CancellationToken.None));

            var download = Assert.IsType<PhysicalFileResult>(await controller.Download(batchId, jobId, CancellationToken.None));
            Assert.Equal(reportPath, download.FileName);
            Assert.Equal("Example_SBI.docx", download.FileDownloadName);
            Assert.IsType<NotFoundResult>(await controller.Download(Guid.NewGuid(), jobId, CancellationToken.None));
        }
        finally { File.Delete(reportPath); }
    }

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

    [Fact]
    public async Task Uploaded_request_creates_a_durable_litigation_assignment_then_generates_without_MCA_calls()
    {
        await using var db = CreateContext();
        var ai = new AssignmentAi();
        var reader = new BorrowerRequestDocumentReader(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var environment = new TestEnvironment();
        var service = new PreLoginReportJobService(db, new PreLoginReportQueue(), new PreLoginReportService(NeverCalledClient(), environment), environment, new BorrowerAssignmentIntake(reader, ai));
        var batch = await service.QueueRequestAsync(null, "Borrower Name: Example Trust", CancellationToken.None);
        var job = (await service.HistoryAsync(batch, CancellationToken.None)).Single();
        var id = job.PreLoginReportJobId;
        string? source = BorrowerAssignmentIntake.PendingSource(job.DataJson)?.SourceStoragePath;
        try
        {
            Assert.True(File.Exists(source));
            Assert.Equal(PreLoginReportJobStatus.Queued, job.Status);
            await service.ProcessAsync(id, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.AwaitingReview, job.Status);
            Assert.Contains("Confirm", job.FailureReason);
            var data = service.AssignmentData(job)!;
            Assert.True(data.Company.LitigationOnly);
            Assert.Null(data.LegalCases);
            Assert.Equal("Example Trust", data.Assignment!.CompanyDetails.CompanyName);
            await service.ProcessAsync(id, CancellationToken.None);
            Assert.Equal(1, ai.Calls);
            await Assert.ThrowsAsync<PreLoginReportException>(() => service.CompleteAssignmentAsync(batch, id, "Example Trust", "Trust", null, null, true, false, CancellationToken.None));
            await Assert.ThrowsAsync<PreLoginReportException>(() => service.CompleteAssignmentAsync(Guid.NewGuid(), id, "Example Trust", "Trust", null, null, true, true, CancellationToken.None));
            await Assert.ThrowsAsync<PreLoginReportException>(() => service.CompleteAssignmentAsync(batch, id, "Example Trust", "Trust", null, null, false, true, CancellationToken.None));
            await service.CompleteAssignmentAsync(batch, id, "Example Trust", "Trust", null, null, true, true, CancellationToken.None);
            await service.ProcessAsync(id, CancellationToken.None);
            job = (await service.FindAsync(id, CancellationToken.None))!;
            Assert.Equal(PreLoginReportJobStatus.Completed, job.Status);
            Assert.True(File.Exists(job.ReportStoragePath));
            await service.RerunAsync(batch, id, CancellationToken.None);
            await service.ProcessAsync(id, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.Completed, job.Status);
            Assert.Equal(1, ai.Calls);
        }
        finally
        {
            await CleanupGeneratedReportAsync(id);
            if (source is not null && File.Exists(source)) File.Delete(source);
        }
    }

    [Fact]
    public async Task Recognition_failure_preserves_the_uploaded_request_for_retry()
    {
        await using var db = CreateContext();
        var environment = new TestEnvironment();
        var reader = new BorrowerRequestDocumentReader(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var service = new PreLoginReportJobService(db, new PreLoginReportQueue(), new PreLoginReportService(NeverCalledClient(), environment), environment,
            new BorrowerAssignmentIntake(reader, new AssignmentAi { Fail = true }));
        var batch = await service.QueueRequestAsync(null, "Borrower Name: Example Trust", CancellationToken.None);
        var job = (await service.HistoryAsync(batch, CancellationToken.None)).Single();
        var source = BorrowerAssignmentIntake.PendingSource(job.DataJson)!.SourceStoragePath!;
        try
        {
            await service.ProcessAsync(job.PreLoginReportJobId, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.Queued, job.Status);
            Assert.Equal(1, job.AttemptCount);
            Assert.NotNull(job.NextAttemptUtc);
            Assert.True(File.Exists(source));
            Assert.Equal(source, BorrowerAssignmentIntake.PendingSource(job.DataJson)!.SourceStoragePath);
        }
        finally { if (File.Exists(source)) File.Delete(source); }
    }

    [Fact]
    public async Task OCR_name_resolves_unique_master_CIN_but_waits_for_type_confirmation()
    {
        await using var db = CreateContext();
        var suffix = Random.Shared.Next(100000, 999999);
        var cin = $"U12345MH2026PTC{suffix}";
        var name = $"ZZOCR {Guid.NewGuid():N} PRIVATE LIMITED".ToUpperInvariant();
        var normalized = CompanyNameNormalizer.Normalize(name);
        db.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = cin, RecordType = CompanyMasterRecordType.Company,
            Name = name, NameNormalized = normalized.NameNormalized, NameCore = normalized.NameCore, EntityForm = normalized.EntityForm, Status = "Active" });
        await db.SaveChangesAsync();
        var handler = new CountingReportHandler(name);
        var environment = new TestEnvironment();
        var ai = new CompanyAssignmentAi(name);
        var reader = new BorrowerRequestDocumentReader(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var reports = new PreLoginReportService(new InstaFinancialsClient(new HttpClient(handler),
            Options.Create(new InstaFinancialsOptions { ApiKey = "test" })), environment);
        var service = new PreLoginReportJobService(db, new PreLoginReportQueue(), reports, environment,
            new BorrowerAssignmentIntake(reader, ai), new BorrowerAssignmentIdentityResolver(db));
        var batch = await service.QueueRequestAsync(null, name, CancellationToken.None);
        var job = (await service.HistoryAsync(batch, CancellationToken.None)).Single();
        var source = BorrowerAssignmentIntake.PendingSource(job.DataJson)!.SourceStoragePath!;
        try
        {
            await service.ProcessAsync(job.PreLoginReportJobId, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.AwaitingReview, job.Status);
            Assert.Equal(0, handler.Calls);
            Assert.Equal(cin, job.Cin);
            Assert.Equal(ResolutionReasonCodes.AutoSelected, service.AssignmentData(job)!.IdentityResolution!.ReasonCode);
            await Assert.ThrowsAsync<PreLoginReportException>(() => service.CompleteAssignmentAsync(batch, job.PreLoginReportJobId,
                name, "Private Limited", cin, null, false, false, CancellationToken.None));
            await service.CompleteAssignmentAsync(batch, job.PreLoginReportJobId,
                name, "Private Limited", cin, null, false, true, CancellationToken.None);
            await service.ProcessAsync(job.PreLoginReportJobId, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.Completed, job.Status);
            Assert.Equal(cin, job.Cin);
            Assert.Equal(1, handler.Calls);
            Assert.Contains(cin, handler.LastUrl);
            var data = service.AssignmentData(job)!;
            Assert.Equal(cin, data.Assignment!.CompanyDetails.Cin);
            Assert.Equal(ResolutionReasonCodes.AutoSelected, data.IdentityResolution!.ReasonCode);
            Assert.True(File.Exists(job.ReportStoragePath));
        }
        finally
        {
            await CleanupGeneratedReportAsync(job.PreLoginReportJobId);
            if (File.Exists(source)) File.Delete(source);
            await db.CompanyMasterRecords.Where(x => x.Identifier == cin).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Exact_name_twins_remain_awaiting_review_without_requesting_details()
    {
        await using var db = CreateContext();
        var name = $"ZZOCR {Guid.NewGuid():N} PRIVATE LIMITED".ToUpperInvariant();
        var normalized = CompanyNameNormalizer.Normalize(name);
        var cin1 = $"U12345MH2026PTC{Random.Shared.Next(100000, 999999)}";
        var cin2 = $"U12345DL2026PTC{Random.Shared.Next(100000, 999999)}";
        foreach (var cin in new[] { cin1, cin2 })
            db.CompanyMasterRecords.Add(new CompanyMasterRecord { Identifier = cin, RecordType = CompanyMasterRecordType.Company,
                Name = name, NameNormalized = normalized.NameNormalized, NameCore = normalized.NameCore,
                EntityForm = normalized.EntityForm, Status = "Active" });
        await db.SaveChangesAsync();
        var handler = new CountingReportHandler(name);
        var environment = new TestEnvironment();
        var reader = new BorrowerRequestDocumentReader(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var service = new PreLoginReportJobService(db, new PreLoginReportQueue(),
            new PreLoginReportService(new InstaFinancialsClient(new HttpClient(handler), Options.Create(new InstaFinancialsOptions { ApiKey = "test" })), environment),
            environment, new BorrowerAssignmentIntake(reader, new CompanyAssignmentAi(name)), new BorrowerAssignmentIdentityResolver(db));
        var batch = await service.QueueRequestAsync(null, name, CancellationToken.None);
        var job = (await service.HistoryAsync(batch, CancellationToken.None)).Single();
        var source = BorrowerAssignmentIntake.PendingSource(job.DataJson)!.SourceStoragePath!;
        try
        {
            await service.ProcessAsync(job.PreLoginReportJobId, CancellationToken.None);
            await db.Entry(job).ReloadAsync();
            Assert.Equal(PreLoginReportJobStatus.AwaitingReview, job.Status);
            Assert.Equal(0, handler.Calls);
            var candidates = service.AssignmentData(job)!.IdentityResolution!.Candidates;
            Assert.Contains(candidates, x => x.Identifier == cin1);
            Assert.Contains(candidates, x => x.Identifier == cin2);
            await Assert.ThrowsAsync<PreLoginReportException>(() => service.CompleteAssignmentAsync(batch, job.PreLoginReportJobId,
                name, "Private Limited", "U12345MH2026PTC000000", null, false, true, CancellationToken.None));
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            await db.CompanyMasterRecords.Where(x => x.Identifier == cin1 || x.Identifier == cin2).ExecuteDeleteAsync();
        }
    }

    private sealed class CompanyAssignmentAi(string name) : IBorrowerAssignmentAiExtractor
    {
        public Task<(BorrowerAssignmentDetails Details, string Model)> ExtractAsync(string text, CancellationToken ct, string? imagePath = null) =>
            Task.FromResult((new BorrowerAssignmentDetails { CompanyDetails = new() { CompanyName = name, EntityType = "Private Limited" } }, "gemini-3.1-flash-lite"));
    }

    private sealed class CountingReportHandler(string name) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public string? LastUrl { get; private set; }
        public bool PauseBeforeResponse { get; init; }
        public Task Started => _started.Task;
        public void Release() => _release.TrySetResult();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            _started.TrySetResult();
            if (PauseBeforeResponse) await _release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { ReportData = new { companyData = new { company = name }, indexChargesData = Array.Empty<object>(), directorData = Array.Empty<object>() } })) };
        }
    }

    private sealed class AssignmentAi : IBorrowerAssignmentAiExtractor
    {
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public Task<(BorrowerAssignmentDetails Details, string Model)> ExtractAsync(string text, CancellationToken ct, string? imagePath = null)
        {
            Calls++;
            if (Fail) throw new PreLoginReportException("Transient recognition failure", retryable: true);
            return Task.FromResult((new BorrowerAssignmentDetails { CompanyDetails = new() { CompanyName = "Example Trust", EntityType = "Trust" } }, "gemini-3.1-flash-lite"));
        }
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
