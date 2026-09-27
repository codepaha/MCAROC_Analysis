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

    private static LitigationStartService Starter(AppDbContext db, PipelineOptions? adminOptions = null, LitigationReuseService? reuse = null)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(adminOptions ?? new PipelineOptions()), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!,
            Options.Create(new BprLitigationOptions()), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(),
            Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        return new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, reuse: reuse);
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

    /// <summary>#291 wiring: StartSearchAsync itself checks for a reusable source before ever admitting a
    /// fresh purchase — no code path outside LitigationReuseService is a second place this rule could drift.</summary>
    [Fact]
    public async Task A_reusable_source_is_taken_instead_of_a_fresh_admission()
    {
        var request = await SeedRequestAsync();
        var scopeKey = PaidCallScopeKeys.LitigationSearch(PaidCallScopeKeys.CanonicalIdentifier(request), Keywords.Select(k => k.Value));

        await using (var seed = CreateContext())
        {
            var sourceRequest = await SeedRequestAsync();
            var job = new LitigationSearchJob { RequestId = sourceRequest.RequestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
            seed.LitigationSearchJobs.Add(job);
            await seed.SaveChangesAsync();
            var snapshot = new LitigationReportSnapshot
            {
                LitigationSearchJobId = job.LitigationSearchJobId, RequestId = sourceRequest.RequestId, ReportHash = "reuse-hash",
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

        await using var db = CreateContext();
        var reuse = new LitigationReuseService(db, new StubEnv(), TimeProvider.System, new LitigationOrderDocumentQueue(), NullLogger<LitigationReuseService>.Instance);
        var result = await Starter(db, reuse: reuse).StartSearchAsync(request, Keywords, "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);

        Assert.True(result.Started);
        Assert.Contains("reused from another request", result.Message);
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == request.RequestId)); // no spend
        Assert.True(await db.LitigationReportSnapshots.AnyAsync(s => s.RequestId == request.RequestId && s.ReusedFromRequestId != null));
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

    private static async Task<McaRequest> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LSS" + Guid.NewGuid().ToString("N")[..7], ClientName = "Start Service Co", CreatedDate = DateTime.UtcNow };
        // A unique CIN per test keeps search scopes independent across tests sharing the database.
        var cin = $"U{Random.Shared.Next(10000, 99999)}OR1995PLC{Random.Shared.Next(100000, 999999)}";
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Start Service Test Co",
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
