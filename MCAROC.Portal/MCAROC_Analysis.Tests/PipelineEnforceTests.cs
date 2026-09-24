using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Enforce mode (owner, 2026-09-24): the coordinator starts the litigation search itself, exactly once,
/// through the admission path — and never in Observe mode, never over an existing search, never again before a
/// refused start is due. Real SQLEXPRESS test database; each test uses its own company.</summary>
public sealed class PipelineEnforceTests : IAsyncLifetime
{
    private static readonly BprLitigationOptions Bpr = new() { BaseUrl = "https://bpr.test", Id = "id", SecretKey = "secret", DefaultEntityType = "company" };
    private static readonly PipelineOptions Enforcing = new()
    {
        Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Litigation = true }, Policy = { LitigationSearch = true },
        // The cap counter is per day across the whole shared test database — keep it out of these tests' way.
        Caps = { LitigationSearchPerDay = 1_000_000 }
    };

    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);
    private readonly List<long> _requestIds = [];

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
        foreach (var id in _requestIds)
        {
            await db.PaidCallAdmissions.Where(a => a.RequestId == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, PaidCallAdmissionState.Released));
            // Parked jobs would otherwise be picked up by other tests' refresh/unlock workers and alerts.
            await db.AutoFetchJobs.Where(j => j.RequestId == id && j.Status == AutoFetchJobStatus.WaitingForUnlock)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, AutoFetchJobStatus.Failed));
        }
    }

    private PipelineReconciler Reconciler(AppDbContext db, PipelineOptions options, IPipelineActions? actions) =>
        new(db, new PipelineSnapshotReader(db, new ConfigurationBuilder().Build(), Options.Create(Bpr),
                Options.Create(new MCAROC_Analysis.Services.AutoFetch.ReferenceToolOptions())),
            _time, NullLogger<PipelineReconciler>.Instance, new StaticOptionsMonitor(options), actions);

    private static PipelineActions RealActions(AppDbContext db)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(Enforcing), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!,
            Options.Create(Bpr), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(),
            Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        return new PipelineActions(new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(Bpr)));
    }

    private async Task<bool> ReconcileAsync(long runId, PipelineOptions options, IPipelineActions? actions = null)
    {
        await using var db = CreateContext();
        return await Reconciler(db, options, actions ?? RealActions(db)).ReconcileAsync(runId, CancellationToken.None);
    }

    private void NextTick() => _time.Advance(PipelineReconciler.MinReconcileInterval + TimeSpan.FromSeconds(1));

    [Fact]
    public async Task Enforce_starts_the_litigation_search_once_through_admission_and_records_the_step()
    {
        var (requestId, runId) = await SeedIngestedRequestWithRunAsync();

        Assert.True(await ReconcileAsync(runId, Enforcing));
        NextTick();
        Assert.True(await ReconcileAsync(runId, Enforcing));

        await using var db = CreateContext();
        var job = await db.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == requestId);
        var admission = await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == requestId);
        Assert.Equal(PaidCallTrigger.Auto, admission.Trigger);
        Assert.Equal(job.LitigationSearchJobId, admission.ReferenceId);

        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
        Assert.Equal(PipelineStageStateKind.Running, stage.State);
        Assert.Equal(job.LitigationSearchJobId, stage.SourceRef);
        Assert.Equal(1, await db.PipelineEvents.CountAsync(e => e.PipelineRunId == runId && e.Action == PipelineEventActions.AutoStarted));

        var run = await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId);
        Assert.Null(run.ReconcileLeaseToken); // released after acting
    }

    [Fact]
    public async Task Observe_mode_never_starts_anything_even_with_actions_available()
    {
        var (requestId, runId) = await SeedIngestedRequestWithRunAsync();
        var observing = new PipelineOptions { Enabled = true, Mode = PipelineMode.Observe, Enforce = { Litigation = true }, Policy = { LitigationSearch = true } };
        var actions = new RecordingActions(PipelineActionResult.Deferred("SHOULD_NOT_BE_CALLED", null));

        Assert.True(await ReconcileAsync(runId, observing, actions));

        Assert.Equal(0, actions.Calls);
        await using var db = CreateContext();
        Assert.False(await db.LitigationSearchJobs.AnyAsync(j => j.RequestId == requestId));
        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
        Assert.Equal(PipelineDecider.ReadyToStart, stage.ReasonCode);
    }

    [Fact]
    public async Task Enforce_without_the_litigation_family_does_not_start_it()
    {
        var (_, runId) = await SeedIngestedRequestWithRunAsync();
        var noFamily = new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce, Policy = { LitigationSearch = true } };
        var actions = new RecordingActions(PipelineActionResult.Deferred("SHOULD_NOT_BE_CALLED", null));

        Assert.True(await ReconcileAsync(runId, noFamily, actions));

        Assert.Equal(0, actions.Calls);
    }

    [Fact]
    public async Task A_refused_start_is_recorded_and_not_retried_before_it_is_due()
    {
        var (_, runId) = await SeedIngestedRequestWithRunAsync();
        var actions = new RecordingActions(PipelineActionResult.Deferred("COST_CAP_REACHED", "Today's automatic litigation search limit has been reached."));

        Assert.True(await ReconcileAsync(runId, Enforcing, actions));
        NextTick();
        Assert.True(await ReconcileAsync(runId, Enforcing, actions));
        Assert.Equal(1, actions.Calls);

        await using (var db = CreateContext())
        {
            var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
            Assert.Equal(PipelineStageStateKind.NotStarted, stage.State);
            Assert.Equal("COST_CAP_REACHED", stage.ReasonCode);
            Assert.NotNull(stage.NextAttemptUtc);
        }

        _time.Advance(TimeSpan.FromMinutes(Enforcing.AutoStartRetryMinutes + 1));
        Assert.True(await ReconcileAsync(runId, Enforcing, actions));
        Assert.Equal(2, actions.Calls);

        await using var verify = CreateContext();
        // The same refusal twice is one step in the timeline, not two.
        Assert.Equal(1, await verify.PipelineEvents.CountAsync(e => e.PipelineRunId == runId && e.Action == PipelineEventActions.AutoStartDeferred));
    }

    [Fact]
    public async Task A_failing_start_is_deferred_and_the_run_is_still_released()
    {
        var (_, runId) = await SeedIngestedRequestWithRunAsync();
        var actions = new RecordingActions(null, new InvalidOperationException("boom"));

        Assert.True(await ReconcileAsync(runId, Enforcing, actions));

        await using var db = CreateContext();
        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
        Assert.Equal("AUTO_START_FAILED", stage.ReasonCode);
        Assert.Null((await db.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == runId)).ReconcileLeaseToken);
    }

    [Fact]
    public async Task Auto_start_never_resets_a_search_that_already_exists()
    {
        var (requestId, _) = await SeedIngestedRequestWithRunAsync();
        long manualJobId;
        await using (var db = CreateContext())
        {
            var request = await db.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
            var manual = await RealActionsStarter(db).StartSearchAsync(request,
                [new LitigationKeyword("ENFORCE TEST CO", LitigationKeywordSource.LegalName)], "company", "cust", PaidCallTrigger.Manual, CancellationToken.None);
            manualJobId = manual.ReferenceId!.Value;
            // Settle it so the reserved-admission guard isn't what stops the auto start.
            await db.PaidCallAdmissions.Where(a => a.RequestId == requestId).ExecuteUpdateAsync(s => s.SetProperty(a => a.State, PaidCallAdmissionState.Released));
        }

        await using var db2 = CreateContext();
        var result = await RealActions(db2).StartLitigationSearchAsync(requestId, "corr", CancellationToken.None);

        Assert.True(result.AlreadyExists);
        Assert.Equal(manualJobId, result.SourceRef);
        Assert.Equal(1, await db2.PaidCallAdmissions.CountAsync(a => a.RequestId == requestId));
        Assert.Equal(manualJobId, (await db2.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.RequestId == requestId)).LitigationSearchJobId);
    }

    [Fact]
    public async Task Auto_start_refuses_a_request_without_a_cin()
    {
        var (requestId, _) = await SeedIngestedRequestWithRunAsync(cin: null);
        await using var db = CreateContext();

        var result = await RealActions(db).StartLitigationSearchAsync(requestId, "corr", CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("LITIGATION_NOT_ELIGIBLE", result.ReasonCode);
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == requestId));
    }

    [Fact]
    public async Task Status_returns_the_step_timeline_in_order()
    {
        var (requestId, runId) = await SeedIngestedRequestWithRunAsync();
        Assert.True(await ReconcileAsync(runId, Enforcing));

        await using var db = CreateContext();
        var controller = new MCAROC_Analysis.Controllers.PipelineController(db, new StaticOptionsMonitor(Enforcing))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.Status(requestId, CancellationToken.None));
        var body = System.Text.Json.JsonSerializer.SerializeToElement(ok.Value, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.Equal("Enforce", body.GetProperty("mode").GetString());
        var texts = body.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ToList();
        Assert.Contains("Litigation search started automatically", texts);
        // Observed transitions are written before the action, so the start is the last step.
        Assert.Equal("Litigation search started automatically", texts[^1]);
    }

    [Fact]
    public async Task Unlock_alerts_list_each_waiting_company_once_and_skip_companies_already_approved()
    {
        var waitingCin = $"U{Random.Shared.Next(10000, 99999)}UA2026PLC{Random.Shared.Next(100000, 999999)}";
        var approvedCin = $"U{Random.Shared.Next(10000, 99999)}UB2026PLC{Random.Shared.Next(100000, 999999)}";
        var first = await SeedWaitingForUnlockAsync(waitingCin);
        await SeedWaitingForUnlockAsync(waitingCin);
        var approvedRequest = await SeedWaitingForUnlockAsync(approvedCin);
        await using var db = CreateContext();
        db.UnlockApprovals.Add(new UnlockApproval
        {
            Identifier = approvedCin, RequestId = approvedRequest, ApprovedBy = "test", Reason = "test",
            ApprovedUtc = DateTime.UtcNow, ExpiresUtc = DateTime.MaxValue
        });
        await db.SaveChangesAsync();

        var controller = new MCAROC_Analysis.Controllers.PipelineController(db, new StaticOptionsMonitor(Enforcing))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.UnlockAlerts(CancellationToken.None));
        var alerts = Assert.IsAssignableFrom<IEnumerable<MCAROC_Analysis.Controllers.UnlockAlertDto>>(ok.Value).ToList();

        var mine = Assert.Single(alerts, a => a.Identifier == waitingCin);
        Assert.Equal(2, mine.WaitingRequests);
        Assert.Equal(first, mine.RequestId);
        Assert.DoesNotContain(alerts, a => a.Identifier == approvedCin);
    }

    [Fact]
    public async Task Unlock_alerts_are_not_crowded_out_by_many_waiting_jobs_for_one_company()
    {
        // Review note on #288: grouping after a 100-job cut hid every company past the first 100 jobs.
        var busyCin = $"U{Random.Shared.Next(10000, 99999)}UC2026PLC{Random.Shared.Next(100000, 999999)}";
        var laterCin = $"U{Random.Shared.Next(10000, 99999)}UD2026PLC{Random.Shared.Next(100000, 999999)}";
        await SeedWaitingForUnlockAsync(busyCin, count: 101);
        await SeedWaitingForUnlockAsync(laterCin);

        await using var db = CreateContext();
        var controller = new MCAROC_Analysis.Controllers.PipelineController(db, new StaticOptionsMonitor(Enforcing))
        {
            ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
        };
        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await controller.UnlockAlerts(CancellationToken.None));
        var alerts = Assert.IsAssignableFrom<IEnumerable<MCAROC_Analysis.Controllers.UnlockAlertDto>>(ok.Value).ToList();

        Assert.Equal(101, Assert.Single(alerts, a => a.Identifier == busyCin).WaitingRequests);
        Assert.Single(alerts, a => a.Identifier == laterCin);
    }

    private async Task<long> SeedWaitingForUnlockAsync(string cin, int count)
    {
        long first = 0;
        for (var i = 0; i < count; i++)
        {
            var id = await SeedWaitingForUnlockAsync(cin);
            if (i == 0) first = id;
        }
        return first;
    }

    private async Task<long> SeedWaitingForUnlockAsync(string cin)
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "PLU" + Guid.NewGuid().ToString("N")[..7], ClientName = "Unlock Alert Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Unlock Alert Company", Cin = cin,
            RequestNumber = $"PLU-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requestIds.Add(request.RequestId);
        db.AutoFetchJobs.Add(new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = cin, Bid = "b" + request.RequestId, Status = AutoFetchJobStatus.WaitingForUnlock,
            StatusMessage = "The company is locked.", CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public void Timeline_text_reads_as_steps()
    {
        Assert.Equal("Litigation search started automatically",
            PipelineEventText.Describe(new PipelineEvent { Stage = PipelineStage.Litigation, Action = PipelineEventActions.AutoStarted }));
        Assert.Equal("Unlock: needs attention — company is locked; approval needed to unlock (1 credit)",
            PipelineEventText.Describe(new PipelineEvent { Stage = PipelineStage.Unlock, Action = PipelineEventActions.Observed(PipelineStageStateKind.NeedsAttention), ReasonCode = "UNLOCK_APPROVAL_REQUIRED" }));
        Assert.Equal("Ingestion: waiting — awaiting fetch",
            PipelineEventText.Describe(new PipelineEvent { Stage = PipelineStage.Ingest, Action = PipelineEventActions.Observed(PipelineStageStateKind.Waiting), ReasonCode = "AWAITING_FETCH" }));
    }

    private static LitigationStartService RealActionsStarter(AppDbContext db)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(Enforcing), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!,
            Options.Create(Bpr), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(),
            Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        return new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(Bpr));
    }

    /// <summary>An analysed manual-upload request (ingestion done, so the litigation search is ready) with a run
    /// whose policy wants the search. Unique CIN per test: admission scopes are company-level.</summary>
    private async Task<(long RequestId, long RunId)> SeedIngestedRequestWithRunAsync(string? cin = "unique")
    {
        await using var db = CreateContext();
        if (cin == "unique") cin = $"U{Random.Shared.Next(10000, 99999)}EN2026PLC{Random.Shared.Next(100000, 999999)}";
        var client = new Client { ClientCode = "PLE" + Guid.NewGuid().ToString("N")[..7], ClientName = "Pipeline Enforce Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Enforce Test Company", Cin = cin,
            RequestNumber = $"PLE-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requestIds.Add(request.RequestId);

        var ingestion = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
        db.IngestionRuns.Add(ingestion);
        await db.SaveChangesAsync();
        request.LatestCompletedIngestionRunId = ingestion.IngestionRunId;
        db.AnalysisRuns.Add(new AnalysisRun
        {
            RequestId = request.RequestId, IngestionRunId = ingestion.IngestionRunId, RunNumber = 1,
            Status = AnalysisRunStatus.Completed, StartedDate = DateTime.UtcNow, CompletedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var adopter = new PipelineAdopter(db, new StaticOptionsMonitor(Enforcing), TimeProvider.System, NullLogger<PipelineAdopter>.Instance);
        var runId = (await adopter.EnsureRunAsync(request.RequestId, PipelineRunTrigger.ManualUpload, null, CancellationToken.None))!.Value;
        return (request.RequestId, runId);
    }

    private sealed class RecordingActions(PipelineActionResult? result, Exception? throws = null) : IPipelineActions
    {
        public int Calls { get; private set; }

        public Task<PipelineActionResult> StartLitigationSearchAsync(long requestId, string correlationId, CancellationToken ct)
        {
            Calls++;
            return throws is not null ? Task.FromException<PipelineActionResult>(throws) : Task.FromResult(result!);
        }
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }
}
