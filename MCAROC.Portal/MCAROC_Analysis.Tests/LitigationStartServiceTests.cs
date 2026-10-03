using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>The reviewer's search/analysis buttons now go through the admission ledger (#265): every start
/// is recorded, a rerun first settles the previous admission, and a failed or redundant start gives its
/// slot back. Real SQL Server; no BPR/Vertex calls are made (only job/run creation is exercised).</summary>
public sealed class LitigationStartServiceTests : IAsyncLifetime
{
    private static readonly IReadOnlyList<LitigationKeyword> Keywords =
        [new("START SERVICE TEST CO", LitigationKeywordSource.LegalName)];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static LitigationStartService Starter(AppDbContext db, PipelineOptions? adminOptions = null, LitigationReuseService? reuse = null,
        BprLitigationOptions? bpr = null)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(adminOptions ?? new PipelineOptions()), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!,
            Options.Create(new BprLitigationOptions()), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(),
            Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        return new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(bpr ?? new BprLitigationOptions()), reuse);
    }

    [Fact]
    public async Task Manual_search_is_admitted_linked_to_its_job_and_counted()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();

        var result = await Starter(db).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(result.Started);
        var admission = await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId);
        Assert.Equal(PaidCallAdmissionState.Reserved, admission.State);
        Assert.Equal(PaidCallTrigger.Manual, admission.Trigger);
        Assert.Equal(result.ReferenceId, admission.ReferenceId);
        Assert.StartsWith($"search|{request.Cin}|", admission.ScopeKey);
    }

    /// <summary>A report another request bought and committed under <paramref name="scopeKey"/>, an hour ago.</summary>
    private static async Task SeedCommittedReportAsync(string scopeKey)
    {
        await using var seed = CreateContext();
        var sourceRequest = await SeedRequestAsync();
        var job = new LitigationSearchJob { RequestId = sourceRequest.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
        seed.LitigationSearchJobs.Add(job);
        await seed.SaveChangesAsync();
        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = sourceRequest.RequestId, ReportHash = "reuse-hash-" + Guid.NewGuid().ToString("N"),
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddHours(-1), CreatedUtc = DateTime.UtcNow
        };
        seed.LitigationReportSnapshots.Add(snapshot);
        await seed.SaveChangesAsync();
        seed.PaidCallAdmissions.Add(new PaidCallAdmission
        {
            Kind = PaidCallKind.LitigationSearch, ScopeKey = scopeKey, DayKey = DateOnly.FromDateTime(DateTime.UtcNow),
            Trigger = PaidCallTrigger.Manual, RequestId = sourceRequest.RequestId, State = PaidCallAdmissionState.Committed,
            ReferenceId = job.LitigationSearchJobId, ReservedUtc = DateTime.UtcNow, ResolvedUtc = DateTime.UtcNow
        });
        await seed.SaveChangesAsync();
    }

    private static async Task<(LitigationStartResult Result, AppDbContext Db)> StartWithReuseAsync(McaRequest request, BprLitigationOptions? bpr = null)
    {
        var db = CreateContext();
        var reuse = new LitigationReuseService(db, new StubEnv(), TimeProvider.System, new LitigationOrderDocumentQueue(), NullLogger<LitigationReuseService>.Instance);
        var result = await Starter(db, reuse: reuse, bpr: bpr).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);
        return (result, db);
    }

    /// <summary>#291 wiring: StartSearchAsync itself checks for a reusable source before ever admitting a
    /// fresh purchase — no code path outside LitigationReuseService is a second place this rule could drift. A company is
    /// searched without exact matching, so the report that is equivalent is one found the same way.</summary>
    [Fact]
    public async Task A_reusable_source_is_taken_instead_of_a_fresh_admission()
    {
        var request = await SeedRequestAsync();
        await SeedCommittedReportAsync(PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), Keywords.Select(k => k.Value), exactMatch: false));

        var (result, db) = await StartWithReuseAsync(request);
        await using var _ = db;

        Assert.True(result.Started);
        Assert.Contains("reused from another request", result.Message);
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == request.RequestId)); // no spend
        Assert.True(await db.LitigationReportSnapshots.AnyAsync(s => s.RequestId == request.RequestId && s.ReusedFromRequestId != null));
    }

    /// <summary>PR #383 review: a report bought before companies were searched broadly (every earlier search was exact) must not
    /// stand in for the broader search — it is missing the High Court and district cases it exists to find — for a Company or
    /// an LLP. A fresh search is admitted instead.</summary>
    [Theory]
    [InlineData(EntityType.Company)]
    [InlineData(EntityType.LLP)]
    public async Task A_legacy_exact_report_is_never_reused_for_the_broader_company_search(EntityType entityType)
    {
        var request = await SeedRequestAsync(entityType);
        await SeedCommittedReportAsync(PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), Keywords.Select(k => k.Value))); // the key every earlier search used

        var (result, db) = await StartWithReuseAsync(request);
        await using var _ = db;

        Assert.True(result.Started);
        Assert.NotNull(result.AdmissionId); // a fresh purchase, not a reuse
        Assert.EndsWith("|broad", (await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId)).ScopeKey);
        Assert.False(await db.LitigationReportSnapshots.AnyAsync(s => s.RequestId == request.RequestId && s.ReusedFromRequestId != null));
    }

    /// <summary>The configuration can be switched back (or an individual searched): a broad report is not an exact one either.</summary>
    [Theory]
    [InlineData(EntityType.Company)]
    [InlineData(EntityType.LLP)]
    public async Task A_broad_report_is_never_reused_for_an_exact_search_after_the_setting_is_switched(EntityType entityType)
    {
        var request = await SeedRequestAsync(entityType);
        await SeedCommittedReportAsync(PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), Keywords.Select(k => k.Value), exactMatch: false));

        var (result, db) = await StartWithReuseAsync(request, new BprLitigationOptions { ExactMatchForCompanies = true });
        await using var _ = db;

        Assert.NotNull(result.AdmissionId);
        Assert.DoesNotContain("|broad", (await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId)).ScopeKey);
    }

    /// <summary>Equivalent searches still reuse for free: an exact search takes an exact (legacy-keyed) report, and a broad one a broad report.</summary>
    [Theory]
    [InlineData(EntityType.Company, true)]
    [InlineData(EntityType.LLP, true)]
    [InlineData(EntityType.Company, false)]
    [InlineData(EntityType.LLP, false)]
    public async Task An_equivalent_report_is_reused_for_free_in_either_mode(EntityType entityType, bool exact)
    {
        var request = await SeedRequestAsync(entityType);
        await SeedCommittedReportAsync(PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), Keywords.Select(k => k.Value), exact));

        var (result, db) = await StartWithReuseAsync(request, new BprLitigationOptions { ExactMatchForCompanies = exact });
        await using var _ = db;

        Assert.Null(result.AdmissionId);
        Assert.Contains("reused from another request", result.Message);
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == request.RequestId));
    }

    /// <summary>The mode claimed in the admission's scope is the mode written to the job it queues, which is what registration reads.</summary>
    [Theory]
    [InlineData(EntityType.Company, false)]
    [InlineData(EntityType.LLP, false)]
    [InlineData(EntityType.Company, true)]
    [InlineData(EntityType.LLP, true)]
    public async Task The_queued_job_records_the_same_matching_mode_as_its_admission_scope(EntityType entityType, bool exactForCompanies)
    {
        var request = await SeedRequestAsync(entityType);
        await using var db = CreateContext();

        var result = await Starter(db, bpr: new BprLitigationOptions { ExactMatchForCompanies = exactForCompanies })
            .StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(result.Started);
        var job = await db.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == request.RequestId);
        var scope = (await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId)).ScopeKey;
        Assert.Equal(exactForCompanies, job.ExactMatch);
        Assert.Equal(!exactForCompanies, scope.EndsWith("|broad", StringComparison.Ordinal));
    }

    [Fact]
    public void The_scope_key_keeps_the_legacy_form_for_exact_matching_and_differs_for_a_broad_search()
    {
        var exact = PaidCallScopeKeys.LitigationSearch("U12345OR1995PLC000001", ["Example Co"]);
        var explicitExact = PaidCallScopeKeys.LitigationSearch("U12345OR1995PLC000001", ["Example Co"], exactMatch: true);
        var broad = PaidCallScopeKeys.LitigationSearch("U12345OR1995PLC000001", ["Example Co"], exactMatch: false);

        Assert.Equal(exact, explicitExact);
        Assert.NotEqual(exact, broad);
        Assert.Equal(exact + "|broad", broad);
    }

    private sealed class StubEnv : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
    }

    [Fact]
    public async Task Second_click_while_the_first_search_is_still_queued_is_refused_without_resetting_the_job()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();
        var first = await Starter(db).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        await using var db2 = CreateContext();
        var second = await Starter(db2).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        Assert.False(second.Started);
        Assert.Equal(AdmissionDenial.InFlight, second.Denial);
        Assert.Equal(1, await db2.PaidCallAdmissions.CountAsync(a => a.RequestId == request.RequestId));
        Assert.Equal(first.ReferenceId, (await db2.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == request.RequestId)).LitigationSearchJobId);
    }

    [Fact]
    public async Task Rerun_after_an_attempted_search_commits_the_previous_admission_before_resetting_the_job()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();
        var first = await Starter(db).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);
        await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == first.ReferenceId).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, LitigationSearchJobStatus.Completed)
            .SetProperty(j => j.RegistrationAttemptedUtc, DateTime.UtcNow));

        await using var db2 = CreateContext();
        var rerun = await Starter(db2).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(rerun.Started);
        var admissions = await db2.PaidCallAdmissions.AsNoTracking().Where(a => a.RequestId == request.RequestId)
            .OrderBy(a => a.PaidCallAdmissionId).ToListAsync();
        Assert.Equal([PaidCallAdmissionState.Committed, PaidCallAdmissionState.Reserved], admissions.Select(a => a.State));
        var job = await db2.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == request.RequestId);
        Assert.Null(job.RegistrationAttemptedUtc); // reset only after the evidence was recorded
    }

    /// <summary>PR #280 review: different keyword sets hash to different scopes, so the scope claim alone
    /// can't stop two starts for the same request — and there is only one search-job row per request.</summary>
    [Fact]
    public async Task Concurrent_searches_with_different_keyword_sets_for_one_request_start_exactly_once()
    {
        var request = await SeedRequestAsync();
        const int starts = 8;

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, starts).Select(i => Task.Run(async () =>
        {
            gate.Wait();
            await using var db = CreateContext();
            IReadOnlyList<LitigationKeyword> keywords = [new($"START SERVICE TEST CO VARIANT {i}", LitigationKeywordSource.LegalName)];
            try
            {
                return await Starter(db).StartSearchAsync(request, keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // A loser that slipped past admission and then hit the job-reset guard — still a second admission.
                return new LitigationStartResult(false, null, null, "reset refused");
            }
        })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Single(results, r => r.Started);
        Assert.All(results.Where(r => !r.Started), r => Assert.Equal(AdmissionDenial.InFlight, r.Denial));
        await using var verify = CreateContext();
        var admissions = await verify.PaidCallAdmissions.AsNoTracking().Where(a => a.RequestId == request.RequestId).ToListAsync();
        var admission = Assert.Single(admissions);
        Assert.Equal(PaidCallAdmissionState.Reserved, admission.State);
        var job = await verify.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == request.RequestId);
        Assert.Equal(job.LitigationSearchJobId, admission.ReferenceId);
    }

    [Fact]
    public async Task Search_start_that_fails_after_admission_releases_the_slot()
    {
        var request = await SeedRequestAsync();
        // A job that may still have a BPR call in flight but no admission (created before the ledger existed)
        // makes CreateOrResetJobAsync refuse.
        await using (var seed = CreateContext())
        {
            seed.LitigationSearchJobs.Add(new LitigationSearchJob
            {
                RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Polling,
                VendorJobId = "vendor-1", CreatedUtc = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var db = CreateContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Starter(db).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None));

        var admission = await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId);
        Assert.Equal(PaidCallAdmissionState.Released, admission.State);
        var scope = await db.SpendScopes.AsNoTracking().SingleAsync(s => s.Kind == PaidCallKind.LitigationSearch && s.ScopeKey == admission.ScopeKey);
        Assert.Null(scope.ActiveAdmissionId);
    }

    [Fact]
    public async Task Analysis_start_is_admitted_once_and_a_second_click_joins_the_active_run_for_free()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();
        var first = await Starter(db).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        await using var db2 = CreateContext();
        var second = await Starter(db2).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(first.Started);
        Assert.True(second.Started);
        Assert.Equal(first.ReferenceId, second.ReferenceId);
        var admission = await db2.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId);
        Assert.Equal(PaidCallKind.LitigationAnalysis, admission.Kind);
        Assert.Equal(first.ReferenceId, admission.ReferenceId);
        Assert.Equal($"analysis|request:{request.RequestId}", admission.ScopeKey); // no completed snapshot yet
    }

    [Fact]
    public async Task Manual_analysis_with_no_completed_snapshot_persists_no_snapshot_linkage()
    {
        var request = await SeedRequestAsync();
        await using var db = CreateContext();

        var result = await Starter(db).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.LitigationAiAnalysisRunId == result.ReferenceId);
        Assert.Equal(LitigationAiAnalysisTrigger.Manual, run.Trigger);
        Assert.Null(run.TriggerSnapshotId);
        Assert.Null(run.OriginSnapshotId);
    }

    /// <summary>Seeds one case that already has a completed case analysis (evidence hash matching its current
    /// evidence) and a completed portfolio synthesis — i.e. a request that has been fully analysed.</summary>
    private async Task<(McaRequest Request, long CaseId)> SeedFullyAnalysedRequestAsync()
    {
        var request = await SeedRequestAsync();
        await using var seed = CreateContext();
        var litCase = new LitigationCase { RequestId = request.RequestId, Court = "High Court", CaseNumber = "CASE-1", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow };
        seed.LitigationCases.Add(litCase);
        await seed.SaveChangesAsync();

        var input = LitigationCaseAnalysisInputBuilder.Build(litCase, new Dictionary<long, LitigationOrderDocument>(), request.CompanyName,
            [request.CompanyName], DateOnly.FromDateTime(DateTime.UtcNow));
        var json = input.EvidenceJson;
        var run = new LitigationAiAnalysisRun { RequestId = request.RequestId, RunNumber = 1, Status = LitigationAiAnalysisRunStatus.Completed, CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow };
        seed.LitigationAiAnalysisRuns.Add(run);
        await seed.SaveChangesAsync();
        seed.LitigationCaseAiAnalyses.Add(new LitigationCaseAiAnalysis
        {
            LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, LitigationCaseId = litCase.LitigationCaseId,
            Status = LitigationAiAnalysisItemStatus.Completed, EvidenceJson = json,
            EvidenceHash = input.EvidenceHash,
            PromptHash = LitigationCaseAnalysisInputBuilder.Hash(LitigationCaseAnalysisPrompt.Instructions),
            AnalysisJson = "{\"summary\":\"ok\"}", CompletedUtc = DateTime.UtcNow
        });
        seed.LitigationPortfolioAiAnalyses.Add(new LitigationPortfolioAiAnalysis
        {
            LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, Status = LitigationAiAnalysisItemStatus.Completed,
            AnalysisJson = "{\"summary\":\"ok\"}", CompletedUtc = DateTime.UtcNow
        });
        await seed.SaveChangesAsync();
        return (request, litCase.LitigationCaseId);
    }

    [Fact]
    public async Task Manual_analysis_is_refused_without_spending_when_every_case_is_already_analysed()
    {
        var (request, _) = await SeedFullyAnalysedRequestAsync();
        await using var db = CreateContext();

        var result = await Starter(db).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("already been analysed", result.Message);
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == request.RequestId));
        Assert.Equal(1, await db.LitigationAiAnalysisRuns.CountAsync(r => r.RequestId == request.RequestId));
    }

    [Fact]
    public async Task Manual_analysis_is_refused_when_the_earlier_result_was_InsufficientEvidence_and_nothing_changed()
    {
        // No order text yet: the case and the portfolio both finish as InsufficientEvidence. That is a finished
        // outcome, so clicking again must not create another run until the evidence changes.
        var (request, _) = await SeedFullyAnalysedRequestAsync();
        await using (var mutate = CreateContext())
        {
            await mutate.LitigationCaseAiAnalyses.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisItemStatus.InsufficientEvidence));
            await mutate.LitigationPortfolioAiAnalyses.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisItemStatus.InsufficientEvidence));
        }
        await using var db = CreateContext();

        var result = await Starter(db).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("already been analysed", result.Message);
        Assert.Equal(1, await db.LitigationAiAnalysisRuns.CountAsync(r => r.RequestId == request.RequestId));
    }

    [Fact]
    public async Task Manual_analysis_runs_again_once_a_case_changes()
    {
        var (request, caseId) = await SeedFullyAnalysedRequestAsync();
        await using (var mutate = CreateContext())
        {
            var c = await mutate.LitigationCases.SingleAsync(x => x.LitigationCaseId == caseId);
            c.CaseStatus = "Disposed";
            c.CaseStage = "Judgment";
            c.LastHearingDate = "2026-09-01";
            c.NextHearingDate = null;
            await mutate.SaveChangesAsync();
        }
        await using var db = CreateContext();

        var result = await Starter(db).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(result.Started);
    }

    [Fact]
    public async Task Auto_analysis_persists_the_snapshot_as_both_trigger_and_origin_and_scopes_admission_by_it()
    {
        var request = await SeedRequestAsync();
        long snapshotId;
        await using (var seed = CreateContext())
        {
            var job = new LitigationSearchJob { RequestId = request.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
            seed.LitigationSearchJobs.Add(job);
            await seed.SaveChangesAsync();
            var snapshot = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = "h1", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow };
            seed.LitigationReportSnapshots.Add(snapshot);
            await seed.SaveChangesAsync();
            snapshotId = snapshot.LitigationReportSnapshotId;
        }

        await using var db = CreateContext();
        var adminOptions = new PipelineOptions { Caps = { LitigationAnalysisPerDay = 1_000_000 } };
        var result = await Starter(db, adminOptions).StartAnalysisAsync(request.RequestId, request.ClientId, PaidCallTrigger.Auto, CancellationToken.None);

        Assert.True(result.Started);
        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.LitigationAiAnalysisRunId == result.ReferenceId);
        Assert.Equal(LitigationAiAnalysisTrigger.Auto, run.Trigger);
        Assert.Equal(snapshotId, run.TriggerSnapshotId);
        Assert.Equal(snapshotId, run.OriginSnapshotId);
        var admission = await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == request.RequestId);
        Assert.Equal($"analysis|{snapshotId}", admission.ScopeKey);
    }

    private static async Task<McaRequest> SeedRequestAsync(EntityType entityType = EntityType.Company)
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LSS" + Guid.NewGuid().ToString("N")[..7], ClientName = "Start Service Co", CreatedDate = DateTime.UtcNow };
        // A unique CIN per test keeps search scopes independent across tests sharing the database.
        var cin = $"U{Random.Shared.Next(10000, 99999)}OR1995PLC{Random.Shared.Next(100000, 999999)}";
        var request = new McaRequest
        {
            Client = client, EntityType = entityType, CompanyName = "Start Service Test Co",
            Cin = cin, RequestNumber = $"LSS-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }
}
