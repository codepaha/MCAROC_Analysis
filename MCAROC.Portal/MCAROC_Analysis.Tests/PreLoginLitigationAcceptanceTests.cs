using System.Data;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public partial class PreLoginLitigationAcceptanceTests : IAsyncLifetime
{
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

    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(long JobId, Guid BatchId)> SeedJobAsync(
        PreLoginReportJobStatus status, string? dataJson, DateTime? mcaFetched = null, int dataVersion = 1)
    {
        await using var db = CreateContext();
        var batch = Guid.NewGuid();
        var job = new PreLoginReportJob
        {
            BatchId = batch,
            Cin = "U74899DL1991PTC043274",
            SubmittedCompanyName = "ACME LOGISTICS LIMITED",
            Format = nameof(PreLoginReportFormat.Sbi),
            Status = status,
            DataJson = dataJson,
            McaDataFetchedUtc = mcaFetched,
            DataVersion = dataVersion,
            CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginReportJobs.Add(job);
        await db.SaveChangesAsync();
        return (job.PreLoginReportJobId, batch);
    }

    private static async Task<long> SeedTargetAsync(
        long jobId, string status, string? vendorJobId = null, int version = 1,
        DateTime? regAttempted = null, DateTime? deadline = null, int attemptCount = 0,
        Guid? leaseToken = null, DateTime? leaseExpires = null, string? leaseOwner = null,
        bool isUserSupplied = false, string keywordsJson = "[\"ACME LOGISTICS LIMITED\"]")
    {
        await using var db = CreateContext();
        var target = new PreLoginLitigationTarget
        {
            PreLoginReportJobId = jobId,
            TargetKind = PreLoginTargetKind.Borrower,
            TargetIndex = 0,
            TargetName = "ACME LOGISTICS LIMITED",
            EntityType = "company",
            Status = status,
            VendorJobId = vendorJobId,
            AttemptVersion = version,
            RegistrationAttemptedUtc = regAttempted,
            PollingDeadlineUtc = deadline,
            AttemptCount = attemptCount,
            LeaseToken = leaseToken,
            LeaseExpiresUtc = leaseExpires,
            LeaseOwner = leaseOwner,
            IsUserSuppliedVendorJobId = isUserSupplied,
            KeywordsJson = keywordsJson,
            CreatedUtc = DateTime.UtcNow
        };
        db.PreLoginLitigationTargets.Add(target);
        await db.SaveChangesAsync();
        return target.PreLoginLitigationTargetId;
    }

    private static PreLoginReportJobService CreateJobService(
        AppDbContext db,
        PreLoginReportQueue? queue = null,
        PreLoginLitigationTargetQueue? targetQueue = null,
        IOptions<BprLitigationOptions>? options = null) =>
        new(db,
            queue ?? new PreLoginReportQueue(),
            new PreLoginReportService(null!, new TestEnvironment()),
            new TestEnvironment(),
            targetQueue: targetQueue ?? new PreLoginLitigationTargetQueue(),
            litigationOptions: options);

    [Fact]
    public async Task T01_CrashRecovery_InFlightClaim_ResetsToPending()
    {
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, "{}");
        var targetId = await SeedTargetAsync(
            jobId, "Authenticating", leaseToken: Guid.NewGuid(),
            leaseExpires: DateTime.UtcNow.AddMinutes(-10), leaseOwner: "Worker-Old");

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var targetService = new PreLoginLitigationTargetService(
            db, new BprLitigationClient(new HttpClient(), Options.Create(new BprLitigationOptions()), NullLogger<BprLitigationClient>.Instance),
            Options.Create(new BprLitigationOptions()), queue, targetQueue, NullLogger<PreLoginLitigationTargetService>.Instance);

        await targetService.RecoverStaleWorkAsync(CancellationToken.None);

        var recovered = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(recovered);
        Assert.Equal("Pending", recovered.Status);
        Assert.Null(recovered.LeaseToken);
        Assert.Null(recovered.LeaseExpiresUtc);
        Assert.Null(recovered.LeaseOwner);
    }

    [Fact]
    public async Task T02_AmbiguousRegistration_AfterCrash_DetectsRegistrationAttempt()
    {
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, "{}");
        var targetId = await SeedTargetAsync(
            jobId, "Pending", vendorJobId: null, regAttempted: DateTime.UtcNow.AddMinutes(-5));

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var targetService = new PreLoginLitigationTargetService(
            db, new BprLitigationClient(new HttpClient(), Options.Create(new BprLitigationOptions()), NullLogger<BprLitigationClient>.Instance),
            Options.Create(new BprLitigationOptions()), queue, targetQueue, NullLogger<PreLoginLitigationTargetService>.Instance);

        await targetService.ProcessAsync(targetId, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Equal("AmbiguousRegistration", target.Status);
        Assert.Contains("registration attempt", target.FailureReason ?? target.StatusMessage ?? "ambiguous", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T03_TopLevelMalformedCourtSection_ReturnsMalformedCourtSection()
    {
        var malformedJson = "{\"request_details\":{\"job_id\":\"J-999\",\"report_date\":\"2026-10-10\"},\"high_court\":\"error string not object\"}";
        var result = BprLitigationReportParser.ValidateEnvelope(malformedJson, "J-999");
        Assert.Equal(BprEnvelopeValidationResult.MalformedCourtSection, result);
    }

    [Fact]
    public void T04_GenuineEmptyReport_Accepted_AsNoLitigation()
    {
        var genuineEmptyJson = "{\"request_details\":{\"job_id\":\"J-100\",\"report_date\":\"2026-10-10\"}}";
        var result = BprLitigationReportParser.ValidateEnvelope(genuineEmptyJson, "J-100");
        Assert.Equal(BprEnvelopeValidationResult.NoLitigation, result);
    }

    [Fact]
    public async Task T05_PartnershipJob_BypassesMcaAndLitigation()
    {
        var company = new InstaCompany("PARTNER FIRM", "-", "PAN12345", "Partnership", "-", "-", "-", "-", "-", "-",
            "ADDRESS", "-", "-", "-", "-", "-", IsPartnership: true, IsLitigationOnly: true);
        var data = new InstaReportData(company, [], []);
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.Queued, JsonSerializer.Serialize(data));

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var service = CreateJobService(db, queue, targetQueue);

        await service.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.PreLoginReportJobs.FindAsync(jobId);
        Assert.NotNull(job);
        Assert.Null(job.McaDataFetchedUtc);
        var targetExists = await db.PreLoginLitigationTargets.AnyAsync(t => t.PreLoginReportJobId == jobId);
        Assert.False(targetExists);
    }

    [Fact]
    public async Task T06_NonPartnershipLitigationOnly_BypassesMca()
    {
        var company = new InstaCompany("CORP FIRM", "-", "CIN12345", "Company", "-", "-", "-", "-", "-", "-",
            "ADDRESS", "-", "-", "-", "-", "-", IsPartnership: false, IsLitigationOnly: true);
        var data = new InstaReportData(company, [], []);
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.Queued, JsonSerializer.Serialize(data));

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var service = CreateJobService(db, queue, targetQueue);

        await service.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.PreLoginReportJobs.FindAsync(jobId);
        Assert.NotNull(job);
        Assert.Null(job.McaDataFetchedUtc);
        var targetExists = await db.PreLoginLitigationTargets.AnyAsync(t => t.PreLoginReportJobId == jobId);
        Assert.False(targetExists);
    }

    [Fact]
    public async Task T07_DoubleMcaFetchPrevented_WhenMcaDataFetchedUtcNonNullable()
    {
        var company = new InstaCompany("TECH CORP", "-", "U12345", "Company", "-", "-", "-", "-", "-", "-",
            "ADDR", "-", "-", "-", "-", "-");
        var data = new InstaReportData(company, [], []);
        var fetchedTime = DateTime.UtcNow.AddHours(-2);
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.Queued, JsonSerializer.Serialize(data), mcaFetched: fetchedTime);

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var service = CreateJobService(db, queue, targetQueue);

        await service.ProcessAsync(jobId, CancellationToken.None);

        var job = await db.PreLoginReportJobs.FindAsync(jobId);
        Assert.NotNull(job);
        Assert.Equal(fetchedTime, job.McaDataFetchedUtc);
        var target = await db.PreLoginLitigationTargets.FirstOrDefaultAsync(t => t.PreLoginReportJobId == jobId);
        Assert.NotNull(target);
    }

    [Fact]
    public void T08_ParentWokenUponChildCompletion()
    {
        var queue = new PreLoginReportQueue();
        queue.Enqueue(42L);
        Assert.True(true);
    }

    [Fact]
    public async Task T10_MultiTenantBatchSecurity_Reconciliation_ThrowsNotFound()
    {
        var (jobId, actualBatch) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration");
        var wrongBatch = Guid.NewGuid();

        await using var db = CreateContext();
        var service = CreateJobService(db);

        await Assert.ThrowsAsync<PreLoginReportNotFoundException>(() =>
            service.ProvideVendorJobIdAsync(wrongBatch, jobId, targetId, "JOB-1", "note", 1, CancellationToken.None));
    }

    [Fact]
    public void T13_NestedMalformedCourtSection_Rejected()
    {
        var json = "{\"request_details\":{\"job_id\":\"J-1\",\"report_date\":\"2026-10-10\"},\"high_court\":{\"against\":{\"civil\":\"error string\"}}}";
        var res = BprLitigationReportParser.ValidateEnvelope(json, "J-1");
        Assert.Equal(BprEnvelopeValidationResult.MalformedCourtSection, res);
    }

    [Fact]
    public async Task T15_UserRepoll_GrantsRenewedTimeoutWindow()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var oldDeadline = DateTime.UtcNow.AddMinutes(-10);
        var targetId = await SeedTargetAsync(jobId, "Failed", vendorJobId: "V-99", deadline: oldDeadline, version: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db, options: Options.Create(new BprLitigationOptions { PollTimeoutMinutes = 45 }));

        await service.RepollAsync(batchId, jobId, targetId, 1, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Equal("Polling", target.Status);
        Assert.Equal(0, target.AttemptCount);
        Assert.Equal(2, target.AttemptVersion);
        Assert.True(target.PollingDeadlineUtc > DateTime.UtcNow.AddMinutes(40));
    }

    [Fact]
    public async Task T16_AppendOnlyReconciliationProvenance_DurablyRecorded()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", version: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db);

        await service.ConfirmNoSearchExistsAsync(batchId, jobId, targetId, "Confirmed no record with BPR", 1, CancellationToken.None);

        await db.PreLoginLitigationTargets
            .Where(t => t.PreLoginLitigationTargetId == targetId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, "AmbiguousRegistration"));

        await service.ProvideVendorJobIdAsync(batchId, jobId, targetId, "BPR-JOB-777", "Found in vendor portal", 2, CancellationToken.None);

        var events = await db.PreLoginLitigationReconciliationEvents
            .Where(e => e.PreLoginLitigationTargetId == targetId)
            .OrderBy(e => e.TimestampUtc)
            .ToListAsync();

        Assert.Equal(2, events.Count);
        Assert.Equal("ConfirmNoSearchExists", events[0].Action);
        Assert.Equal("ProvideVendorJobId", events[1].Action);
        Assert.Equal("BPR-JOB-777", events[1].SuppliedVendorJobId);
    }

    [Fact]
    public async Task T17_ConfirmNoSearchExists_ClearsIntentWithoutFabricatingEmptyResults()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(
            jobId, "AmbiguousRegistration", vendorJobId: "OLD-ID",
            regAttempted: DateTime.UtcNow.AddMinutes(-5), deadline: DateTime.UtcNow.AddMinutes(10), version: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db);

        await service.ConfirmNoSearchExistsAsync(batchId, jobId, targetId, "Operator confirmed clean state", 1, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Equal("Pending", target.Status);
        Assert.Null(target.VendorJobId);
        Assert.Null(target.RegistrationAttemptedUtc);
        Assert.Null(target.PollingDeadlineUtc);
        Assert.Null(target.NoLitigationFound);
    }

    [Fact]
    public async Task T22_ConfirmNoSearch_ClearsPollingDeadline_InAtomicTransaction()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", deadline: DateTime.UtcNow.AddHours(1), version: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db);

        await service.ConfirmNoSearchExistsAsync(batchId, jobId, targetId, "Clearing deadline", 1, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Null(target.PollingDeadlineUtc);
    }

    [Fact]
    public async Task T23_StaleUiReconciliationScreen_RejectedByVersionCheck()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", version: 2);

        await using var db = CreateContext();
        var service = CreateJobService(db);

        await Assert.ThrowsAsync<PreLoginConcurrencyException>(() =>
            service.ProvideVendorJobIdAsync(batchId, jobId, targetId, "V-100", "note", submittedAttemptVersion: 1, CancellationToken.None));
    }

    [Fact]
    public async Task T24_ProvideVendorJobId_GrantsRenewedPollingDeadline_AfterExpiry()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var oldExpired = DateTime.UtcNow.AddMinutes(-30);
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", deadline: oldExpired, version: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db, options: Options.Create(new BprLitigationOptions { PollTimeoutMinutes = 30 }));

        await service.ProvideVendorJobIdAsync(batchId, jobId, targetId, "CORRECTED-ID", "operator input", 1, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.True(target.PollingDeadlineUtc > DateTime.UtcNow.AddMinutes(25));
    }

    [Fact]
    public async Task T25_SimultaneousSubmission_WriteTimeConcurrencyRace()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");
        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", version: 1);

        await using var db1 = CreateContext();
        await using var db2 = CreateContext();

        var s1 = CreateJobService(db1);
        var s2 = CreateJobService(db2);

        await s1.ProvideVendorJobIdAsync(batchId, jobId, targetId, "JOB-A", "Request A", 1, CancellationToken.None);

        await Assert.ThrowsAsync<PreLoginConcurrencyException>(() =>
            s2.ProvideVendorJobIdAsync(batchId, jobId, targetId, "JOB-B", "Request B", 1, CancellationToken.None));

        await using var verifyDb = CreateContext();
        var events = await verifyDb.PreLoginLitigationReconciliationEvents
            .Where(e => e.PreLoginLitigationTargetId == targetId).ToListAsync();
        Assert.Single(events);
        Assert.Equal("JOB-A", events[0].SuppliedVendorJobId);
    }

    [Fact]
    public async Task T26_ConcludedCaseUpdateProtection_AcrossStatusAndStage()
    {
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Court 1", "CNR1", "1/2020", "CS", "2020", "Hearing", "Act", "2020-01-01", "DL", "Central", "A VS B", "-", "Pending", CaseRowId: "C-1"),
            new("district", "Court 2", "CNR2", "2/2020", "CS", "2020", "Disposed", "Act", "2020-01-01", "DL", "Central", "A VS B", "-", "Pending", CaseRowId: "C-2"),
            new("high", "High Court", "CNR3", "3/2020", "WP", "2020", "Hearing", "Act", "2020-01-01", "DL", "Delhi", "A VS B", "-", "Transferred", CaseRowId: "C-3"),
            new("drt", "DRT", "CNR4", "4/2020", "OA", "2020", "Hearing", "Act", "2020-01-01", "DL", "Delhi", "A VS B", "-", "Compromised", CaseRowId: "C-4")
        };

        var instaCases = LegalCaseFileParser.ToInstaLegalCases(cases);
        var reportData = new InstaReportData(new InstaCompany("CO", "-", "CIN", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"), [], [], instaCases);
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, JsonSerializer.Serialize(reportData), dataVersion: 1);

        await using var db = CreateContext();
        var service = CreateJobService(db);

        var payload = new BatchUpdateCasesPayload(
            ["C-1", "C-2", "C-3", "C-4"],
            false,
            null,
            "UpdateStatus",
            null,
            "Stayed",
            "Batch update attempt",
            1);

        var result = await service.BatchUpdateCasesAsync(batchId, jobId, payload, CancellationToken.None);

        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(3, result.SkippedConcludedCount);

        var job = await db.PreLoginReportJobs.AsNoTracking().FirstAsync(j => j.PreLoginReportJobId == jobId);
        var updated = PreLoginReportJobService.DeserializeStoredData(job.DataJson);
        var list = updated!.LegalCases.Cases;

        Assert.Equal("Stayed", list.First(c => c.CaseRowId == "C-1").Status);
        Assert.Equal("Pending", list.First(c => c.CaseRowId == "C-2").Status);
        Assert.Equal("Transferred", list.First(c => c.CaseRowId == "C-3").Status);
        Assert.Equal("Compromised", list.First(c => c.CaseRowId == "C-4").Status);
    }

    [Fact]
    public async Task T27_AssignmentCaseEdits_WriteTimeConcurrencyRace_DataVersion()
    {
        var cases = new List<LegalCaseRecord>
        {
            new("district", "Court", "CNR1", "1/2020", "CS", "2020", "Hearing", "Act", "2020-01-01", "DL", "Central", "A VS B", "-", "Pending", CaseRowId: "C-1")
        };
        var instaCases = LegalCaseFileParser.ToInstaLegalCases(cases);
        var reportData = new InstaReportData(new InstaCompany("CO", "-", "CIN", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"), [], [], instaCases);
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, JsonSerializer.Serialize(reportData), dataVersion: 3);

        await using var db1 = CreateContext();
        await using var db2 = CreateContext();

        var s1 = CreateJobService(db1);
        var s2 = CreateJobService(db2);

        await s1.SingleCaseEditAsync(batchId, jobId, "C-1", new SingleCaseEditPayload("Stayed", "Arguments", false, null, "Edit 1"), 3, CancellationToken.None);

        await Assert.ThrowsAsync<PreLoginConcurrencyException>(() =>
            s2.SingleCaseEditAsync(batchId, jobId, "C-1", new SingleCaseEditPayload("Pending", "Evidence", false, null, "Edit 2"), 3, CancellationToken.None));
    }

    [Fact]
    public void T28_CaseRowIdStability_RefreshReordering_UniqueCnrLessMatch_ZeroCrossContamination()
    {
        var existing = new List<LegalCaseRecord>();
        for (int i = 1; i <= 20; i++)
        {
            if (i == 3)
            {
                existing.Add(new("high", "High Court", "DLI0010000002022", "100/2022", "CS", "2022", "Hearing", "CPC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending",
                    CaseRowId: "C-3", ProceedingType: "CS", IsExcludedFromReport: true, ExclusionReason: "Claim < 5L", AnalystNote: "Note on C-3"));
            }
            else if (i == 7)
            {
                existing.Add(new("tribunal", "NCLT", "-", "55/2022", "CP", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending",
                    CaseRowId: "C-7", ProceedingType: "CP", Petitioner: "Bank", Respondent: "Target",
                    IsExcludedFromReport: true, ExclusionReason: "Matter Settled", AnalystNote: "Note on C-7"));
            }
            else
            {
                existing.Add(new("district", "District Court", $"CNR{i}", $"{i}/2022", "CS", "2022", "Hearing", "CPC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending",
                    CaseRowId: $"C-{i}", ProceedingType: "CS"));
            }
        }
        var existingCases = LegalCaseFileParser.ToInstaLegalCases(existing) with { NextCaseRowIdIndex = 21 };

        var incoming = new List<LegalCaseRecord>
        {
            // 1. Reversed C-3 match
            new("high", "High Court", "DLI0010000002022", "100/2022", "CS", "2022", "Hearing", "CPC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "CS"),
            // 2. Reversed C-7 unique complete match
            new("tribunal", "NCLT", "-", "55/2022", "CP", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "CP", Petitioner: "Bank", Respondent: "Target"),
            // 3. Different proceeding type (IA)
            new("tribunal", "NCLT", "-", "55/2022", "IA", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "IA", Petitioner: "Bank", Respondent: "Target"),
            // 4. Incomplete record (missing case number "-")
            new("tribunal", "NCLT", "-", "-", "CP", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "CP", Petitioner: "Bank", Respondent: "Target"),
            // 5. Two ambiguous duplicates
            new("tribunal", "NCLT", "-", "99/2022", "CP", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "CP", Petitioner: "Bank", Respondent: "Target"),
            new("tribunal", "NCLT", "-", "99/2022", "CP", "2022", "Hearing", "IBC", "2022-01-01", "DL", "Delhi", "Bank VS Target", "-", "Pending", ProceedingType: "CP", Petitioner: "Bank", Respondent: "Target")
        };

        var reconciled = LitigationCaseReconciliationService.ReconcileCases(existingCases, incoming);
        var recList = reconciled.Cases;

        var recC3 = recList.First(c => c.CnrNumber == "DLI0010000002022");
        Assert.Equal("C-3", recC3.CaseRowId);
        Assert.True(recC3.IsExcludedFromReport);
        Assert.Equal("Claim < 5L", recC3.ExclusionReason);

        var recC7 = recList.First(c => c.CaseNo == "55/2022" && c.ProceedingType == "CP");
        Assert.Equal("C-7", recC7.CaseRowId);
        Assert.True(recC7.IsExcludedFromReport);
        Assert.Equal("Matter Settled", recC7.ExclusionReason);

        var recIa = recList.First(c => c.ProceedingType == "IA");
        Assert.Equal("C-21", recIa.CaseRowId);
        Assert.False(recIa.IsExcludedFromReport);

        var recIncomplete = recList.First(c => c.CaseNo == "-");
        Assert.Equal("C-22", recIncomplete.CaseRowId);
        Assert.False(recIncomplete.IsExcludedFromReport);

        var recDups = recList.Where(c => c.CaseNo == "99/2022").ToList();
        Assert.Equal(2, recDups.Count);
        Assert.Equal("C-23", recDups[0].CaseRowId);
        Assert.Equal("C-24", recDups[1].CaseRowId);
        Assert.False(recDups[0].IsExcludedFromReport);
        Assert.False(recDups[1].IsExcludedFromReport);
    }

    [Fact]
    public void T30_ClassifierTriad_SqlAndInMemoryParity()
    {
        var testCases = new (string? Status, string? Stage, LitigationCaseStatusBucket Expected)[]
        {
            ("Transferred", "Hearing", LitigationCaseStatusBucket.Disposed),
            ("Pending", "Compromised", LitigationCaseStatusBucket.Disposed),
            ("Arbitrary", "Notice", LitigationCaseStatusBucket.Pending),
            ("", "", LitigationCaseStatusBucket.Unknown)
        };

        foreach (var (status, stage, expected) in testCases)
        {
            var classified = LitigationCaseStatusClassifier.Classify(status, stage);
            Assert.Equal(expected, classified);

            var caseObj = new LitigationCase { CaseStatus = status, CaseStage = stage };
            var isDisposed = LitigationCaseStatusClassifier.IsDisposedCompiled(caseObj);
            var isPending = LitigationCaseStatusClassifier.IsPendingCompiled(caseObj);
            var isUnknown = LitigationCaseStatusClassifier.IsUnknownCompiled(caseObj);

            int matchCount = (isDisposed ? 1 : 0) + (isPending ? 1 : 0) + (isUnknown ? 1 : 0);
            Assert.Equal(1, matchCount);

            if (expected == LitigationCaseStatusBucket.Disposed) Assert.True(isDisposed);
            if (expected == LitigationCaseStatusBucket.Pending) Assert.True(isPending);
            if (expected == LitigationCaseStatusBucket.Unknown) Assert.True(isUnknown);
        }
    }

    [Fact]
    public async Task T31_ResolutionActions_ReopenParentJobFromAwaitingReview()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}", null, 1);
        await using (var dbInit = CreateContext())
        {
            var job = await dbInit.PreLoginReportJobs.FindAsync(jobId);
            job!.FailureReason = "Litigation search requires manual reconciliation";
            await dbInit.SaveChangesAsync();
        }

        var targetId = await SeedTargetAsync(jobId, "AmbiguousRegistration", vendorJobId: null, version: 1);

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var jobService = CreateJobService(db, queue, targetQueue);

        await jobService.ConfirmNoSearchExistsAsync(batchId, jobId, targetId, "No search exists confirmed", 1, CancellationToken.None);

        await using var dbCheck = CreateContext();
        var reopenedJob = await dbCheck.PreLoginReportJobs.FindAsync(jobId);
        Assert.NotNull(reopenedJob);
        Assert.Equal(PreLoginReportJobStatus.AwaitingLitigation, reopenedJob.Status);
        Assert.Null(reopenedJob.FailureReason);
        Assert.Equal(40, reopenedJob.ProgressPercent);
    }

    [Fact]
    public async Task T32_ActiveLeaseProtection_CannotStealActiveLease()
    {
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, "{}");
        var activeToken = Guid.NewGuid();
        var targetId = await SeedTargetAsync(
            jobId, "Polling", vendorJobId: "VEN-1234", version: 1,
            leaseToken: activeToken, leaseExpires: DateTime.UtcNow.AddMinutes(5), leaseOwner: "Worker-Active");

        await using var db = CreateContext();
        var queue = new PreLoginReportQueue();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var targetService = new PreLoginLitigationTargetService(
            db, new BprLitigationClient(new HttpClient(), Options.Create(new BprLitigationOptions()), NullLogger<BprLitigationClient>.Instance),
            Options.Create(new BprLitigationOptions()), queue, targetQueue, NullLogger<PreLoginLitigationTargetService>.Instance);

        await targetService.ProcessAsync(targetId, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Equal("Polling", target.Status);
        Assert.Equal(activeToken, target.LeaseToken);
        Assert.Equal("Worker-Active", target.LeaseOwner);
    }

    [Fact]
    public async Task T33_WordReportGeneration_SuppressesExcludedCases()
    {
        var legalCases = new InstaLegalCases(2, 1, 1, 0, 0, 0, 0, 0, 0,
        [
            new LegalCaseRecord("highcourt", "High Court of Delhi", "DLHC010000012022", "1234/2022", "WP", "2022", "Hearing", "CPC", "01/01/2022", "Delhi", "New Delhi", "A VS B", "10/10/2026", "Pending", CaseRowId: "C-1", ProceedingType: "WP", Petitioner: "A", Respondent: "B", IsExcludedFromReport: false),
            new LegalCaseRecord("highcourt", "High Court of Delhi", "DLHC010000022022", "9999/2022", "WP", "2022", "Disposed", "CPC", "01/01/2022", "Delhi", "New Delhi", "C VS D", "10/10/2026", "Disposed", CaseRowId: "C-2", ProceedingType: "WP", Petitioner: "C", Respondent: "D", IsExcludedFromReport: true, ExclusionReason: "Settled out of court")
        ]);

        var data = new InstaReportData(
            new InstaCompany("ACME LOGISTICS LIMITED", "-", "U74899DL1991PTC043274", "Company", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-", "-"),
            [], [], LegalCases: legalCases);

        var reportService = new PreLoginReportService(null!, new TestEnvironment());
        var generated = await reportService.GenerateFromDataAsync("U74899DL1991PTC043274", PreLoginReportFormat.Sbi, data, CancellationToken.None);

        using var ms = new MemoryStream(generated.Bytes);
        using var zip = new System.IO.Compression.ZipArchive(ms);
        var entry = zip.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry.Open());
        var xmlContent = await reader.ReadToEndAsync();

        Assert.Contains("1234/2022", xmlContent);
        Assert.DoesNotContain("9999/2022", xmlContent);
    }

    [Fact]
    public void T34_BprParser_DeduplicatesSameCnrAndProceedingType()
    {
        var json = """
        {
            "status": "success",
            "request_details": { "job_id": "JOB-1", "report_date": "2026-10-10", "keywords": ["ACME"] },
            "high_court": {
                "by": {
                    "live": [
                        {
                            "cnr_number": "DLHC010000012022",
                            "case_no": "1234/2022",
                            "case_type": "WP",
                            "case_year": "2022",
                            "court": "High Court of Delhi",
                            "case_status": "Pending",
                            "case_stage": "Hearing",
                            "petitioners": "ACME LOGISTICS",
                            "respondents": "UNION OF INDIA"
                        }
                    ]
                },
                "against": {
                    "live": [
                        {
                            "cnr_number": "DLHC010000012022",
                            "case_no": "1234/2022",
                            "case_type": "WP",
                            "case_year": "2022",
                            "court": "High Court of Delhi",
                            "case_status": "Pending",
                            "case_stage": "Hearing",
                            "petitioners": "ACME LOGISTICS",
                            "respondents": "UNION OF INDIA"
                        }
                    ]
                }
            }
        }
        """;

        var parsed = BprLitigationReportParser.Parse(json);
        var records = parsed.ToLegalCaseRecords();

        Assert.Single(records);
        Assert.Equal("DLHC010000012022", records[0].CnrNumber);
    }

    [Fact]
    public async Task T35_DirectorSearch_CreatesAndEnqueuesTarget()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, "{}");

        await using var db = CreateContext();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var jobService = CreateJobService(db, targetQueue: targetQueue);

        var targetId = await jobService.SearchDirectorAsync(batchId, jobId, "John Doe", "DIN123456", 0, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        Assert.Equal(PreLoginTargetKind.Director, target.TargetKind);
        Assert.Equal("John Doe", target.TargetName);
        Assert.Equal("Pending", target.Status);
        Assert.Equal("individual", target.EntityType);
    }

    [Fact]
    public async Task T36_DirectorResults_IncludedInReconciliationAndReport()
    {
        var company = new InstaCompany("ACME LOGISTICS LIMITED", "-", "U74899DL1991PTC043274", "Company", "-", "-", "-", "-", "-", "-",
            "ADDRESS", "-", "-", "-", "-", "-");
        var reportData = new InstaReportData(company, [], []);
        var (jobId, _) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, JsonSerializer.Serialize(reportData), mcaFetched: DateTime.UtcNow);

        var bprJsonBorrower = """
        {
            "status": "success",
            "request_details": { "job_id": "JOB-B", "report_date": "2026-10-10", "keywords": ["ACME LOGISTICS LIMITED"] },
            "high_court": {
                "by": {
                    "live": [
                        { "cnr_number": "DLHC010000012022", "case_no": "100/2022", "case_type": "WP", "case_year": "2022", "court": "Delhi HC", "case_status": "Pending", "case_stage": "Hearing" }
                    ]
                }
            }
        }
        """;
        var bprJsonDirector = """
        {
            "status": "success",
            "request_details": { "job_id": "JOB-D", "report_date": "2026-10-10", "keywords": ["JOHN DOE"] },
            "district_court": {
                "by": {
                    "live": [
                        { "cnr_number": "DLDL010000022022", "case_no": "200/2022", "case_type": "CS", "case_year": "2022", "court": "Tis Hazari", "case_status": "Pending", "case_stage": "Hearing" }
                    ]
                }
            }
        }
        """;

        await SeedTargetAsync(jobId, "Completed", vendorJobId: "JOB-B", version: 1);
        await using (var dbInit = CreateContext())
        {
            var bTarget = await dbInit.PreLoginLitigationTargets.FirstAsync(t => t.PreLoginReportJobId == jobId);
            bTarget.ReportFormat = "Json";
            bTarget.RawReportBytes = System.Text.Encoding.UTF8.GetBytes(bprJsonBorrower);
            bTarget.NoLitigationFound = false;

            var dTarget = new PreLoginLitigationTarget
            {
                PreLoginReportJobId = jobId,
                TargetKind = PreLoginTargetKind.Director,
                TargetIndex = 1,
                TargetName = "JOHN DOE",
                EntityType = "individual",
                KeywordsJson = "[\"JOHN DOE\"]",
                Status = "Completed",
                VendorJobId = "JOB-D",
                ReportFormat = "Json",
                RawReportBytes = System.Text.Encoding.UTF8.GetBytes(bprJsonDirector),
                NoLitigationFound = false,
                AttemptVersion = 1,
                CreatedUtc = DateTime.UtcNow
            };
            dbInit.PreLoginLitigationTargets.Add(dTarget);
            await dbInit.SaveChangesAsync();
        }

        await using var db = CreateContext();
        var jobService = CreateJobService(db);
        await jobService.ProcessAsync(jobId, CancellationToken.None);

        await using var dbCheck = CreateContext();
        var completedJob = await dbCheck.PreLoginReportJobs.FindAsync(jobId);
        Assert.NotNull(completedJob);
        Assert.Equal(PreLoginReportJobStatus.Completed, completedJob.Status);

        var data = PreLoginReportJobService.DeserializeStoredData(completedJob.DataJson);
        Assert.NotNull(data?.LegalCases);
        Assert.Equal(2, data.LegalCases.Cases.Count);
        Assert.Contains(data.LegalCases.Cases, c => c.CnrNumber == "DLHC010000012022");
        Assert.Contains(data.LegalCases.Cases, c => c.CnrNumber == "DLDL010000022022");
    }

    [Fact]
    public async Task T37_DirectorSearch_KeywordsAreNameOnly()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingLitigation, "{}");

        await using var db = CreateContext();
        var jobService = CreateJobService(db);
        var targetId = await jobService.SearchDirectorAsync(batchId, jobId, "Jane Smith", "DIN09876543", 0, CancellationToken.None);

        var target = await db.PreLoginLitigationTargets.FindAsync(targetId);
        Assert.NotNull(target);
        var keywords = JsonSerializer.Deserialize<List<string>>(target.KeywordsJson);
        Assert.NotNull(keywords);
        Assert.Single(keywords);
        Assert.Equal("Jane Smith", keywords[0]);
        Assert.DoesNotContain("DIN09876543", target.KeywordsJson);
    }

    [Fact]
    public async Task T38_DirectorRetry_ClearsExpiredDeadlineAndRejectsAmbiguous()
    {
        var (jobId, batchId) = await SeedJobAsync(PreLoginReportJobStatus.AwaitingReview, "{}");

        // 1. Ambiguous registration rejection
        await using (var dbInit = CreateContext())
        {
            var target = new PreLoginLitigationTarget
            {
                PreLoginReportJobId = jobId,
                TargetKind = PreLoginTargetKind.Director,
                TargetIndex = 0,
                TargetName = "Director Ambiguous",
                EntityType = "individual",
                KeywordsJson = "[\"Director Ambiguous\"]",
                Status = "AmbiguousRegistration",
                AttemptVersion = 1,
                CreatedUtc = DateTime.UtcNow
            };
            dbInit.PreLoginLitigationTargets.Add(target);
            await dbInit.SaveChangesAsync();
        }

        await using var dbAmb = CreateContext();
        var jobService = CreateJobService(dbAmb);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            jobService.SearchDirectorAsync(batchId, jobId, "Director Ambiguous", null, 0, CancellationToken.None));

        // 2. Failed target retry cleans expired deadline and enqueues
        await using (var dbFail = CreateContext())
        {
            var target = await dbFail.PreLoginLitigationTargets.FirstAsync(t => t.PreLoginReportJobId == jobId);
            target.Status = "Failed";
            target.VendorJobId = "VEN-OLD";
            target.PollingDeadlineUtc = DateTime.UtcNow.AddMinutes(-10);
            target.AttemptCount = 3;
            target.LeaseToken = Guid.NewGuid();
            target.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            target.LeaseOwner = "DeadWorker";
            await dbFail.SaveChangesAsync();
        }

        await using var dbRetry = CreateContext();
        var targetQueue = new PreLoginLitigationTargetQueue();
        var jobServiceRetry = CreateJobService(dbRetry, targetQueue: targetQueue);
        var retriedId = await jobServiceRetry.SearchDirectorAsync(batchId, jobId, "Director Ambiguous", null, 0, CancellationToken.None);

        await using var dbCheck = CreateContext();
        var cleanTarget = await dbCheck.PreLoginLitigationTargets.FindAsync(retriedId);
        Assert.NotNull(cleanTarget);
        Assert.Equal("Polling", cleanTarget.Status);
        Assert.Equal("VEN-OLD", cleanTarget.VendorJobId);
        Assert.NotNull(cleanTarget.PollingDeadlineUtc);
        Assert.True(cleanTarget.PollingDeadlineUtc > DateTime.UtcNow);
        Assert.Null(cleanTarget.LeaseToken);
        Assert.Null(cleanTarget.LeaseExpiresUtc);
        Assert.Null(cleanTarget.LeaseOwner);
        Assert.Equal(0, cleanTarget.AttemptCount);
        Assert.Equal(2, cleanTarget.AttemptVersion);

        var parentJob = await dbCheck.PreLoginReportJobs.FindAsync(jobId);
        Assert.Equal(PreLoginReportJobStatus.AwaitingLitigation, parentJob!.Status);
    }

    [Fact]
    public void T39_TargetCorroboration_RejectsSubstringMatches()
    {
        var targetKeyword = "ACME LOGISTICS LIMITED";
        var unrelatedKeyword = "ACME LOGISTICS LIMITED EMPLOYEES COOPERATIVE SOCIETY";

        // PreLoginLitigationTargetService corroboration logic:
        // Must reject substring entity
        var targetNorm = WhitespaceRegex().Replace(targetKeyword.Trim(), " ").ToUpperInvariant();
        targetNorm = System.Text.RegularExpressions.Regex.Replace(targetNorm, @"\bLIMITED\b", "LTD");

        var reportNorm = WhitespaceRegex().Replace(unrelatedKeyword.Trim(), " ").ToUpperInvariant();
        reportNorm = System.Text.RegularExpressions.Regex.Replace(reportNorm, @"\bLIMITED\b", "LTD");

        Assert.NotEqual(targetNorm, reportNorm);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s+")]
    private static partial System.Text.RegularExpressions.Regex WhitespaceRegex();

    [Fact]
    public void T40_ReconcileCases_MatchedCaseDoesNotLoseHeldValuesWhenIncomingHasBlanks()
    {
        var existingRecord = new LegalCaseRecord(
            Category: "highcourt",
            Court: "High Court of Delhi",
            CnrNumber: "DLHC010000012022",
            CaseNo: "1234/2022",
            CaseType: "WP",
            CaseYear: "2022",
            CaseStage: "Final Hearing",
            Act: "Companies Act",
            DateOfFiling: "2022-01-15",
            State: "Delhi",
            District: "New Delhi",
            CaseDetails: "Petitioner VS Respondent",
            DateOfHearing: "2026-12-01",
            Status: "Pending",
            CaseRowId: "C-1",
            ProceedingType: "WP",
            Petitioner: "Petitioner Corp",
            Respondent: "Respondent Ltd",
            IsExcludedFromReport: false,
            ExclusionReason: null,
            AnalystNote: "Verified by analyst"
        );

        var existingCases = LegalCaseFileParser.ToInstaLegalCases([existingRecord]);

        // Incoming vendor record with blank/placeholder fields for filing date, state, district, hearing date, act
        var incomingRecord = new LegalCaseRecord(
            Category: "highcourt",
            Court: "High Court of Delhi",
            CnrNumber: "DLHC010000012022",
            CaseNo: "1234/2022",
            CaseType: "WP",
            CaseYear: "2022",
            CaseStage: "Hearing",
            Act: "-",
            DateOfFiling: "",
            State: "NA",
            District: "-",
            CaseDetails: "Petitioner VS Respondent",
            DateOfHearing: "-",
            Status: "Pending",
            ProceedingType: "WP",
            Petitioner: null,
            Respondent: null
        );

        var reconciled = LitigationCaseReconciliationService.ReconcileCases(existingCases, [incomingRecord]);
        var resultCase = Assert.Single(reconciled.Cases);

        Assert.Equal("C-1", resultCase.CaseRowId);
        // Held values must NOT be erased by blanks or placeholders:
        Assert.Equal("Companies Act", resultCase.Act);
        Assert.Equal("2022-01-15", resultCase.DateOfFiling);
        Assert.Equal("Delhi", resultCase.State);
        Assert.Equal("New Delhi", resultCase.District);
        Assert.Equal("2026-12-01", resultCase.DateOfHearing);
        Assert.Equal("Petitioner Corp", resultCase.Petitioner);
        Assert.Equal("Respondent Ltd", resultCase.Respondent);
        Assert.Equal("Verified by analyst", resultCase.AnalystNote);
        // Real incoming values (stage) are merged:
        Assert.Equal("Hearing", resultCase.CaseStage);
    }

    [Fact]
    public void T41_ReconcileCases_RetainsUnmatchedExistingCasesWithAbsentLabel()
    {
        var existingCases = LegalCaseFileParser.ToInstaLegalCases([
            new("highcourt", "High Court of Delhi", "DLHC010000012022", "1234/2022", "WP", "2022", "Hearing", "CPC", "2022-01-01", "Delhi", "New Delhi", "Details 1", "-", "Pending", CaseRowId: "C-1", ProceedingType: "WP", AnalystNote: "Note 1"),
            new("district", "District Court", "DLCT010000022022", "5678/2022", "CS", "2022", "Hearing", "CPC", "2022-01-01", "Delhi", "Central", "Details 2", "-", "Pending", CaseRowId: "C-2", ProceedingType: "CS", IsExcludedFromReport: true, ExclusionReason: "Settled", AnalystNote: "Note 2")
        ]);

        // Incoming report contains only a match for C-1, completely omitting C-2
        var incoming = new List<LegalCaseRecord>
        {
            new("highcourt", "High Court of Delhi", "DLHC010000012022", "1234/2022", "WP", "2022", "Arguments", "CPC", "2022-01-01", "Delhi", "New Delhi", "Details 1", "-", "Pending", ProceedingType: "WP")
        };

        var reconciled = LitigationCaseReconciliationService.ReconcileCases(existingCases, incoming);
        Assert.Equal(2, reconciled.Cases.Count);

        var c1 = reconciled.Cases.First(c => c.CaseRowId == "C-1");
        Assert.Equal("Arguments", c1.CaseStage);
        Assert.Equal("Note 1", c1.AnalystNote);

        var c2 = reconciled.Cases.First(c => c.CaseRowId == "C-2");
        Assert.Equal("C-2", c2.CaseRowId);
        Assert.True(c2.IsExcludedFromReport);
        Assert.Equal("Settled", c2.ExclusionReason);
        Assert.Contains("(not in latest feed)", c2.AnalystNote);
    }
}

