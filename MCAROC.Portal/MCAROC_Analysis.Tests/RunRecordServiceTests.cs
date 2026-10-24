using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.E2e;
using MCAROC_Analysis.Services.Preflight;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>#514: a request's run record gathers what a matrix run must record (the pre-flight made before it, every stage, the fetch, the planned
/// keyword list, case counts, dossier downloads) without anyone copying it by hand, and its Markdown carries all of it.</summary>
public sealed class RunRecordServiceTests : IAsyncLifetime
{
    private readonly string _cin = $"U{Random.Shared.Next(10000, 99999)}MH2021PTC{Random.Shared.Next(100000, 999999)}";
    private readonly List<long> _requests = [];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        // AuditLogs is append-only (a trigger refuses deletes); this test's rows are tied to its own requests and times, so they stay.
        await db.LitigationCases.Where(c => _requests.Contains(c.RequestId)).ExecuteDeleteAsync();
        await db.LitigationSearchJobs.Where(j => _requests.Contains(j.RequestId)).ExecuteDeleteAsync();
        await db.AutoFetchJobs.Where(j => _requests.Contains(j.RequestId)).ExecuteDeleteAsync();
        await db.PipelineStageStates.Where(s => _requests.Contains(s.Run!.RequestId)).ExecuteDeleteAsync();
        await db.PipelineRuns.Where(r => _requests.Contains(r.RequestId)).ExecuteDeleteAsync();
        await db.CompanyReportLifecycles.Where(l => l.Identifier == _cin).ExecuteDeleteAsync();
        await db.Requests.Where(r => _requests.Contains(r.RequestId)).ExecuteDeleteAsync();
    }

    private async Task<long> AuditAsync(AppDbContext db, AuditActionType action, DateTime at, long? requestId, object payload)
    {
        var log = new AuditLog
        {
            TimestampUtc = at, CorrelationId = "rr-" + Guid.NewGuid().ToString("N"), ActorType = ActorType.AuthenticatedReviewer, ActorId = "tester",
            Action = action, EventKind = AuditEventKind.DomainLifecycle, Status = AuditStatus.Success, RequestId = requestId,
            EventPayloadJson = AuditLogEntryFactory.SerializeAndCapPayload(payload)
        };
        db.AuditLogs.Add(log);
        await db.SaveChangesAsync();
        return log.AuditLogId;
    }

    [Fact]
    public async Task A_run_record_gathers_the_preflight_stages_fetch_keywords_cases_and_downloads()
    {
        var created = DateTime.UtcNow.AddMinutes(-30);
        long requestId;
        await using (var db = CreateContext())
        {
            var request = new McaRequest
            {
                Client = new Client { ClientCode = "RR" + Guid.NewGuid().ToString("N")[..8], ClientName = "Record Test Bank", CreatedDate = created },
                EntityType = EntityType.LLP, CompanyName = "NATURE EARTH KHARKHODA LLP", Llpin = _cin, RequestNumber = $"RR-{Guid.NewGuid():N}",
                RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = created
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.RequestId;
            _requests.Add(requestId);

            var run = new PipelineRun { RequestId = requestId, Outcome = PipelineOutcome.CompleteWithWarnings, Trigger = PipelineRunTrigger.AutoFetch,
                CorrelationId = "rr", CreatedUtc = created, CoreReadyUtc = created.AddMinutes(5) };
            db.PipelineRuns.Add(run);
            await db.SaveChangesAsync();
            db.PipelineStageStates.AddRange(
                new PipelineStageState { PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Fetch, State = PipelineStageStateKind.Succeeded, UpdatedUtc = created.AddMinutes(4) },
                new PipelineStageState { PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Refresh, State = PipelineStageStateKind.SucceededWithWarnings,
                    ReasonCode = "EXPORTED_BEFORE_REFRESH", ReasonDetail = "Exported the reference tool's current data.", UpdatedUtc = created.AddMinutes(3), Attempts = 1 });
            db.AutoFetchJobs.Add(new AutoFetchJob
            {
                RequestId = requestId, Cin = _cin, Bid = "b", Status = AutoFetchJobStatus.CompletedWithWarnings, CreatedUtc = created,
                RegistryTotalCount = 1354, RegistryListedCount = 378, FilesTotal = 400, FilesDownloaded = 373, FilesFailed = 27, BytesDownloaded = 50 * 1024 * 1024,
                WarningsJson = JsonSerializer.Serialize(new[] { "The reference tool reports 1,354 filing documents but listed 378." })
            });
            db.CompanyReportLifecycles.Add(new CompanyReportLifecycle { Identifier = _cin, State = CompanyReportLifecycleState.Unlocked, UnlockedUtc = created.AddDays(-1) });
            db.LitigationSearchJobs.Add(new LitigationSearchJob
            {
                RequestId = requestId, Status = LitigationSearchJobStatus.Completed, ApplicationCustomerId = "x", CreatedUtc = created, ExactMatch = false,
                KeywordsJson = """[{"value":"NATURE EARTH KHARKHODA LLP","source":"LegalName"},{"value":"NATURE EARTH KHARKHODA","source":"GeneratedLegalSuffixVariant"}]"""
            });
            db.LitigationCases.AddRange(
                new LitigationCase { RequestId = requestId, CaseNumber = "1", Court = "DRT", FirstSeenUtc = created, LastSeenUtc = created, AttributionOutcome = LitigationAttributionOutcome.Attributed },
                new LitigationCase { RequestId = requestId, CaseNumber = "2", Court = "DRT", FirstSeenUtc = created, LastSeenUtc = created, AttributionOutcome = LitigationAttributionOutcome.NotTheCompany });
            await db.SaveChangesAsync();

            var failing = new PreflightReport(created.AddMinutes(-5),
            [
                new PreflightCheck("litigation-api", "Litigation data API", PreflightOutcome.Fail, "203.0.113.7:2087 refused the connection (nothing is listening).", "Ask its team.", 30),
                new PreflightCheck("portal-login", "Portal login", PreflightOutcome.Pass, "Sign-in configured.", null, 1),
            ]);
            await AuditAsync(db, AuditActionType.PreflightRun, created.AddMinutes(-5), null, PreflightRunPayload.From(failing));
            await AuditAsync(db, AuditActionType.PreflightRun, created.AddMinutes(10), null, PreflightRunPayload.From(failing with { Checks = [] })); // after: not this run's
            await AuditAsync(db, AuditActionType.DossierDownloaded, created.AddMinutes(20), requestId, new DossierDownloadPayload("Executive", 312_000, 28));
        }

        await using var read = CreateContext();
        var record = (await new RunRecordService(read).GetAsync(requestId, CancellationToken.None))!;

        Assert.Equal("Fail", record.PreflightBefore!.Overall);
        Assert.Equal(2, record.PreflightBefore.Checks.Count);                     // the one made before the request, not the later one
        Assert.Equal(["Refresh", "Fetch"], record.Stages.Select(s => s.Stage));
        Assert.Equal("Refresh", Assert.Single(record.StagesWithWarnings).Stage);
        Assert.Equal((378, 1354, 27), (record.Fetch!.RegistryListed, record.Fetch.RegistryTotal, record.Fetch.FilesFailed));
        Assert.Equal("Unlocked", record.Lifecycle!.State);
        Assert.Equal(["NATURE EARTH KHARKHODA LLP", "NATURE EARTH KHARKHODA"], record.Litigation!.Keywords.Select(k => k.Value));
        Assert.Equal((2, 1), (record.Litigation.Cases, record.Litigation.CasesShown));
        var download = Assert.Single(record.DossierDownloads);
        Assert.Equal((312_000L, 28), (download.Bytes!.Value, download.Pages!.Value));

        var md = RunRecordService.ToMarkdown(record);
        Assert.Contains("Pre-flight before it: **Fail**", md);
        Assert.Contains("litigation-api Fail", md);
        Assert.Contains("| Refresh | SucceededWithWarnings | EXPORTED_BEFORE_REFRESH |", md);
        Assert.Contains("Registry listed 378 of 1,354", md);
        Assert.Contains("| 2 | NATURE EARTH KHARKHODA | GeneratedLegalSuffixVariant |", md);
        Assert.Contains("Cases 2, shown 1", md);
        Assert.Contains("28 pages", md);
        Assert.Contains("- [ ] Opened and reviewed.", md);
    }

    [Fact]
    public async Task A_request_with_no_preflight_says_so_and_nothing_else_is_invented()
    {
        long requestId;
        await using (var db = CreateContext())
        {
            var request = new McaRequest
            {
                Client = new Client { ClientCode = "RR" + Guid.NewGuid().ToString("N")[..8], ClientName = "Record Test Bank", CreatedDate = DateTime.UtcNow },
                EntityType = EntityType.Company, CompanyName = "Plain Upload Co", Cin = _cin, RequestNumber = $"RR-{Guid.NewGuid():N}",
                RequestStatus = RequestStatus.Created, CreatedDate = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc) // before any pre-flight existed
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            requestId = request.RequestId;
            _requests.Add(requestId);
        }

        await using var read = CreateContext();
        var record = (await new RunRecordService(read).GetAsync(requestId, CancellationToken.None))!;
        var md = RunRecordService.ToMarkdown(record);

        Assert.Null(record.PreflightBefore);
        Assert.Null(record.Fetch);
        Assert.Null(record.Litigation);
        Assert.Contains("Pre-flight before it: **none run**", md);
        Assert.Contains("## Litigation search: not run", md);
        Assert.Contains("- Not downloaded.", md);
    }

    /// <summary>The audit log keeps at most 2,000 characters of payload, and replaces a longer one with a "truncated" marker: a full pre-flight
    /// run with every check failing at length must still fit.</summary>
    [Fact]
    public void A_whole_failing_preflight_run_fits_the_audit_payload_cap()
    {
        var keys = new[] { "reference-session", "mca-company", "mca-llp", "litigation-api", "order-host", "portal-login", "database", "ai", "court-agents", "keywords", "disk" };
        var report = new PreflightReport(DateTime.UtcNow, keys.Select(k => new PreflightCheck(k, k, PreflightOutcome.Fail, new string('x', 400), "fix", 1)).ToList());

        var json = AuditLogEntryFactory.SerializeAndCapPayload(PreflightRunPayload.From(report))!;

        Assert.DoesNotContain("truncated", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(11, JsonSerializer.Deserialize<PreflightRunPayload>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Checks.Count);
    }
}
