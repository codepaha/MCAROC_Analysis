using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
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

    private static readonly PipelineOptions EnforcingAnalysis = new()
    {
        Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Litigation = true, LitigationAnalysis = true },
        Policy = { LitigationSearch = true, LitigationAnalysis = true },
        Caps = { LitigationSearchPerDay = 1_000_000, LitigationAnalysisPerDay = 1_000_000 }
    };

    // Policy.LitigationSearch stays on (but Enforce.Litigation off) purely so the run has an enrichment stage
    // that never reaches "done" — otherwise a manual-upload request with everything else Completed reaches
    // Outcome.Complete on the very first tick and is no longer "live" for a second reconcile to observe.
    private static readonly PipelineOptions EnforcingDossier = new()
    {
        Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Dossier = true }, Policy = { LitigationSearch = true }
    };

    private static readonly PipelineOptions EnforcingRetries = new()
    {
        Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Retries = true }, MaxCoordinatorAttempts = 4
    };

    private static readonly PipelineOptions NotEnforcingRetries = new()
    {
        Enabled = true, Mode = PipelineMode.Enforce, MaxCoordinatorAttempts = 4 // Enforce.Retries left off
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
            _time, NullLogger<PipelineReconciler>.Instance, new StaticOptionsMonitor(options), actions,
            SlotLeases(db));

    private static PipelineActions RealActions(AppDbContext db, PipelineOptions? adminOptions = null)
    {
        var admission = new PaidCallAdmissionService(db, new StaticOptionsMonitor(adminOptions ?? Enforcing), TimeProvider.System, NullLogger<PaidCallAdmissionService>.Instance);
        var search = new LitigationSearchJobService(db, null!, new LitigationSearchQueue(), null!, null!,
            Options.Create(Bpr), NullLogger<LitigationSearchJobService>.Instance);
        var analysis = new LitigationAiAnalysisOrchestrator(db, null!, new LitigationAiAnalysisQueue(),
            Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);
        // Dossier pre-render is exercised separately (RecordingActions below) — none of this file's
        // litigation-focused tests touch it.
        var autoFetch = new MCAROC_Analysis.Services.AutoFetch.AutoFetchJobService(db, null!, Options.Create(new MCAROC_Analysis.Services.AutoFetch.ReferenceToolOptions()),
            null!, null!, null!, null!, null!, null!, NullLogger<MCAROC_Analysis.Services.AutoFetch.AutoFetchJobService>.Instance);
        return new PipelineActions(new LitigationStartService(db, admission, search, new LitigationSearchQueue(), analysis, Options.Create(Bpr)), null!,
            autoFetch, new MCAROC_Analysis.Services.AutoFetch.AutoFetchQueue());
    }

    private async Task<bool> ReconcileAsync(long runId, PipelineOptions options, IPipelineActions? actions = null, PipelineOptions? adminOptions = null)
    {
        await using var db = CreateContext();
        return await Reconciler(db, options, actions ?? RealActions(db, adminOptions ?? options)).ReconcileAsync(runId, CancellationToken.None);
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

    /// <summary>#292 (plan §6.5, hardened after PR #311 review): MaxConcurrentRuns bounds how many requests
    /// may actively search or analyse litigation at once — protecting BPR's/Vertex's own limits from a burst
    /// of requests all becoming ready together. Enforced via the real, sp_getapplock-fenced
    /// <see cref="OperationalSlotLeaseService"/> (same one LargeUpload/LargeUnpack use), not a plain count —
    /// a plain count-then-start races across reconciler instances, which is exactly the gap the review
    /// caught. These tests use that real service directly to seed/inspect/clean up slot holders, and always
    /// release whatever they acquire so a 4-hour-lease slot never leaks into a later test on this shared
    /// database.</summary>
    private static OperationalSlotLeaseService SlotLeases(AppDbContext db) => new(db, NullLogger<OperationalSlotLeaseService>.Instance);

    private async Task<int> ActiveEnrichmentSlotCountAsync()
    {
        await using var db = CreateContext();
        return await db.OperationalSlotLeases.AsNoTracking().CountAsync(l => l.SlotType == PipelineReconciler.LitigationEnrichmentSlot);
    }

    /// <summary>Occupies one slot directly through the same real mechanism the coordinator itself uses —
    /// nothing about the run behind <paramref name="holderId"/> needs to exist; the cap only ever looks at
    /// the lease table. Returns the holder id so the caller can release it when done.</summary>
    private async Task<string> SeedActiveEnrichmentBlockerAsync()
    {
        var holderId = $"blocker:{Guid.NewGuid():N}";
        await using var db = CreateContext();
        var acquired = await SlotLeases(db).TryAcquireSlotAsync(PipelineReconciler.LitigationEnrichmentSlot, holderId, PipelineReconciler.LitigationEnrichmentSlotDuration, int.MaxValue, CancellationToken.None);
        Assert.True(acquired.Success);
        return holderId;
    }

    private async Task ReleaseSlotAsync(string holderId)
    {
        await using var db = CreateContext();
        await SlotLeases(db).ReleaseSlotAsync(PipelineReconciler.LitigationEnrichmentSlot, holderId, CancellationToken.None);
    }

    [Fact]
    public async Task A_new_search_is_deferred_without_ever_calling_the_action_once_the_concurrency_cap_is_reached()
    {
        var ambient = await ActiveEnrichmentSlotCountAsync();
        var blocker = await SeedActiveEnrichmentBlockerAsync();
        try
        {
            var capped = new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Litigation = true }, Policy = { LitigationSearch = true }, MaxConcurrentRuns = ambient + 1 };
            var (_, runId) = await SeedIngestedRequestWithRunAsync();
            var actions = new RecordingActions(PipelineActionResult.Deferred("SHOULD_NOT_BE_CALLED", null));

            Assert.True(await ReconcileAsync(runId, capped, actions));

            Assert.Equal(0, actions.Calls);
            await using var db = CreateContext();
            var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
            Assert.Equal(PipelineStageStateKind.NotStarted, stage.State);
            Assert.Equal("CONCURRENCY_CAP_REACHED", stage.ReasonCode);
            Assert.NotNull(stage.NextAttemptUtc);
            // The refusal never touched the lease table for this run — nothing was reserved to release.
            Assert.Equal(ambient + 1, await ActiveEnrichmentSlotCountAsync());
        }
        finally { await ReleaseSlotAsync(blocker); }
    }

    [Fact]
    public async Task A_new_search_starts_normally_while_comfortably_below_the_concurrency_cap_and_holds_a_real_slot()
    {
        var ambient = await ActiveEnrichmentSlotCountAsync();
        var roomy = new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce, Enforce = { Litigation = true }, Policy = { LitigationSearch = true }, MaxConcurrentRuns = ambient + 10 };
        var (_, runId) = await SeedIngestedRequestWithRunAsync();
        var actions = new RecordingActions(new PipelineActionResult(true, false, 1, null, null));

        Assert.True(await ReconcileAsync(runId, roomy, actions));

        Assert.Equal(1, actions.Calls);
        await using var db = CreateContext();
        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Litigation);
        Assert.Equal(PipelineStageStateKind.Running, stage.State);
        Assert.Equal(PipelineEventActions.AutoStartedCode, stage.ReasonCode);
        // The successful start really did reserve a slot — cap enforcement has teeth, not just bookkeeping.
        Assert.Equal(ambient + 1, await ActiveEnrichmentSlotCountAsync());
        await ReleaseSlotAsync(PipelineReconciler.EnrichmentSlotHolderId(runId, PipelineStage.Litigation));
    }

    [Fact]
    public async Task The_concurrency_cap_is_off_by_default_no_matter_how_much_else_is_running()
    {
        var blocker = await SeedActiveEnrichmentBlockerAsync();
        try
        {
            var (_, runId) = await SeedIngestedRequestWithRunAsync(); // Enforcing: MaxConcurrentRuns left at its 0 default
            var actions = new RecordingActions(new PipelineActionResult(true, false, 1, null, null));

            Assert.True(await ReconcileAsync(runId, Enforcing, actions));

            Assert.Equal(1, actions.Calls);
            // MaxConcurrentRuns=0 never touches the lease mechanism at all — no slot taken for this start.
            Assert.Equal(1, await ActiveEnrichmentSlotCountAsync());
        }
        finally { await ReleaseSlotAsync(blocker); }
    }

    /// <summary>PR #311 review's own regression case: force two starts to race at the cap and confirm the
    /// atomic reservation — not a plain count-then-start — is what actually decides the outcome. A cap of 1
    /// with two callers hammering TryAcquireSlotAsync concurrently must admit exactly one, never both and
    /// never neither; sp_getapplock inside the real service is what makes that true regardless of timing.</summary>
    [Fact]
    public async Task Two_concurrent_starts_racing_at_a_cap_of_one_never_both_get_admitted()
    {
        var ambient = await ActiveEnrichmentSlotCountAsync();
        var cap = ambient + 1;
        var holderA = $"race-a:{Guid.NewGuid():N}";
        var holderB = $"race-b:{Guid.NewGuid():N}";

        await using var dbA = CreateContext();
        await using var dbB = CreateContext();
        var barrier = new TaskCompletionSource();
        var taskA = Task.Run(async () =>
        {
            await barrier.Task;
            return await SlotLeases(dbA).TryAcquireSlotAsync(PipelineReconciler.LitigationEnrichmentSlot, holderA, PipelineReconciler.LitigationEnrichmentSlotDuration, cap, CancellationToken.None);
        });
        var taskB = Task.Run(async () =>
        {
            await barrier.Task;
            return await SlotLeases(dbB).TryAcquireSlotAsync(PipelineReconciler.LitigationEnrichmentSlot, holderB, PipelineReconciler.LitigationEnrichmentSlotDuration, cap, CancellationToken.None);
        });
        barrier.SetResult(); // release both at once rather than however the scheduler happened to queue them
        var (resultA, resultB) = (await taskA, await taskB);

        try
        {
            Assert.NotEqual(resultA.Success, resultB.Success); // exactly one, not both, not neither
            Assert.Equal(cap, await ActiveEnrichmentSlotCountAsync());
        }
        finally
        {
            if (resultA.Success) await ReleaseSlotAsync(holderA);
            if (resultB.Success) await ReleaseSlotAsync(holderB);
        }
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
    public async Task Enforce_starts_litigation_analysis_once_the_search_is_done_and_orders_are_processed()
    {
        var (requestId, runId) = await SeedIngestedRequestWithRunAsync(adoptOptions: EnforcingAnalysis);
        long snapshotId;
        await using (var seed = CreateContext())
        {
            var job = new LitigationSearchJob { RequestId = requestId, KeywordsJson = "[]", Status = LitigationSearchJobStatus.Completed, CreatedUtc = DateTime.UtcNow };
            seed.LitigationSearchJobs.Add(job);
            await seed.SaveChangesAsync();
            var snapshot = new LitigationReportSnapshot
            {
                LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId, ReportHash = Guid.NewGuid().ToString("N")[..16],
                Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CreatedUtc = DateTime.UtcNow
            };
            seed.LitigationReportSnapshots.Add(snapshot);
            await seed.SaveChangesAsync();
            snapshotId = snapshot.LitigationReportSnapshotId;
        }

        Assert.True(await ReconcileAsync(runId, EnforcingAnalysis));
        NextTick();
        Assert.True(await ReconcileAsync(runId, EnforcingAnalysis));

        await using var db = CreateContext();
        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.RequestId == requestId);
        Assert.Equal(LitigationAiAnalysisTrigger.Auto, run.Trigger);
        Assert.Equal(snapshotId, run.OriginSnapshotId);
        var admission = await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == requestId && a.Kind == PaidCallKind.LitigationAnalysis);
        Assert.Equal(PaidCallTrigger.Auto, admission.Trigger);

        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.LitigationAnalysis);
        Assert.Equal(PipelineStageStateKind.Running, stage.State);
        Assert.Equal(run.LitigationAiAnalysisRunId, stage.SourceRef);
    }

    [Fact]
    public async Task Enforce_dossier_pre_renders_exactly_once_when_the_stage_first_becomes_done()
    {
        var (_, runId) = await SeedIngestedRequestWithRunAsync(adoptOptions: EnforcingDossier);
        var actions = new RecordingActions(null);

        Assert.True(await ReconcileAsync(runId, EnforcingDossier, actions));
        Assert.Equal(1, actions.DossierCalls);

        NextTick();
        Assert.True(await ReconcileAsync(runId, EnforcingDossier, actions));
        // Dossier's State stays Succeeded on the second tick (nothing changed) — no repeat call.
        Assert.Equal(1, actions.DossierCalls);
    }

    [Fact]
    public async Task Dossier_pre_render_is_never_attempted_when_the_family_is_off()
    {
        var (_, runId) = await SeedIngestedRequestWithRunAsync();
        var offOptions = new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce };
        var actions = new RecordingActions(null);

        Assert.True(await ReconcileAsync(runId, offOptions, actions));

        Assert.Equal(0, actions.DossierCalls);
    }

    /// <summary>#292 (plan §6.2): the coordinator retries a Transient-classified Fetch failure with backoff,
    /// counting toward Pipeline:MaxCoordinatorAttempts, and gives up with RETRIES_EXHAUSTED once that cap is
    /// hit — never a 5th attempt. Isolated from the real AutoFetchJobService via RecordingActions so this
    /// tests exactly the coordinator's own attempt/backoff bookkeeping, not the requeue call itself (that's
    /// the next test).</summary>
    [Fact]
    public async Task Enforce_retries_a_failed_fetch_with_backoff_then_gives_up_after_the_cap()
    {
        var (requestId, runId) = await SeedFailedFetchRequestWithRunAsync();
        var actions = new RecordingActions(new PipelineActionResult(true, false, 1, null, null));
        var expectedMinutes = new[] { 2.0, 10.0, 30.0, 120.0 };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.True(await ReconcileAsync(runId, EnforcingRetries, actions));
            Assert.Equal(attempt, actions.RetryCalls);

            await using var db = CreateContext();
            var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Fetch);
            Assert.Equal(attempt, stage.Attempts);
            Assert.Equal(PipelineStageStateKind.Running, stage.State);
            Assert.NotNull(stage.NextAttemptUtc);
            var delay = stage.NextAttemptUtc!.Value - _time.GetUtcNow().UtcDateTime;
            Assert.InRange(delay, TimeSpan.FromMinutes(expectedMinutes[attempt - 1] * 0.8), TimeSpan.FromMinutes(expectedMinutes[attempt - 1] * 1.2));

            _time.Advance(delay + TimeSpan.FromSeconds(1));
            // The stage was left "Running" (queued again) — but nothing in this test ever really requeues
            // the DB row via RecordingActions, so the decider still reports FETCH_FAILED next tick, exactly
            // as if the retried attempt failed again just as fast. That's deliberate: it is what lets a
            // single test walk the whole schedule without waiting on anything real.
            await using var reset = CreateContext();
            await reset.AutoFetchJobs.Where(j => j.RequestId == requestId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, AutoFetchJobStatus.Failed));
        }

        // 5th tick, well past the 4th backoff: the cap is reached — no 5th retry call, and the stage now
        // reads RETRIES_EXHAUSTED instead of the raw FETCH_FAILED.
        Assert.True(await ReconcileAsync(runId, EnforcingRetries, actions));
        Assert.Equal(4, actions.RetryCalls);
        await using var final = CreateContext();
        var finalStage = await final.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Fetch);
        Assert.Equal(PipelineStageStateKind.NeedsAttention, finalStage.State);
        Assert.Equal("RETRIES_EXHAUSTED", finalStage.ReasonCode);
    }

    [Fact]
    public async Task Fetch_retry_is_never_attempted_when_the_family_is_off()
    {
        var (_, runId) = await SeedFailedFetchRequestWithRunAsync();
        var actions = new RecordingActions(new PipelineActionResult(true, false, 1, null, null));

        Assert.True(await ReconcileAsync(runId, NotEnforcingRetries, actions));

        Assert.Equal(0, actions.RetryCalls);
        await using var db = CreateContext();
        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Fetch);
        Assert.Equal("FETCH_FAILED", stage.ReasonCode);
        Assert.Equal(0, stage.Attempts);
    }

    /// <summary>End to end with the real AutoFetchJobService/AutoFetchQueue (RealActions, no mock): the
    /// coordinator's retry actually requeues the real job row and hands its id to the real in-process queue —
    /// not just that the reconciler believes it did.</summary>
    [Fact]
    public async Task Enforce_retry_actually_requeues_the_real_auto_fetch_job()
    {
        var (requestId, runId) = await SeedFailedFetchRequestWithRunAsync();

        Assert.True(await ReconcileAsync(runId, EnforcingRetries));

        await using var db = CreateContext();
        var job = await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.RequestId == requestId);
        Assert.Equal(AutoFetchJobStatus.Queued, job.Status);
        Assert.Null(job.FailureReason);
    }

    /// <summary>The coordinator's own bookkeeping (PipelineStageState.Attempts) starts fresh even when the
    /// underlying AutoFetchJob has already burned attempts some other way — e.g. a human clicking "Retry" on
    /// the AutoFetch UI (AutoFetchController.RetryJob), which calls AutoFetchJobService.RequeueAsync directly
    /// and never touches the coordinator. Plan §6.2: internal attempts count toward the same cap regardless
    /// of who triggered them, so a job that already arrives at MaxCoordinatorAttempts worth of AttemptCount
    /// must not get the full schedule again from zero.</summary>
    [Fact]
    public async Task Coordinator_retry_cap_counts_the_workers_own_prior_attempts_too()
    {
        var (_, runId) = await SeedFailedFetchRequestWithRunAsync(jobAttemptCount: 4);
        var actions = new RecordingActions(new PipelineActionResult(true, false, 1, null, null));

        Assert.True(await ReconcileAsync(runId, EnforcingRetries, actions));

        Assert.Equal(0, actions.RetryCalls);
        await using var db = CreateContext();
        var stage = await db.PipelineStageStates.AsNoTracking().SingleAsync(s => s.PipelineRunId == runId && s.Stage == PipelineStage.Fetch);
        Assert.Equal(PipelineStageStateKind.NeedsAttention, stage.State);
        Assert.Equal("RETRIES_EXHAUSTED", stage.ReasonCode);
    }

    /// <summary>An auto-fetch request whose job has already failed once — the Fetch stage's own
    /// NeedsAttention(FETCH_FAILED), the one #292 wires to coordinator-driven retry.</summary>
    private async Task<(long RequestId, long RunId)> SeedFailedFetchRequestWithRunAsync(int jobAttemptCount = 0)
    {
        await using var db = CreateContext();
        var cin = $"U{Random.Shared.Next(10000, 99999)}RF2026PLC{Random.Shared.Next(100000, 999999)}";
        var client = new Client { ClientCode = "PLF" + Guid.NewGuid().ToString("N")[..7], ClientName = "Retry Fetch Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Retry Fetch Company", Cin = cin,
            RequestNumber = $"PLF-{Guid.NewGuid():N}", RequestStatus = RequestStatus.ExtractionFailed, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        _requestIds.Add(request.RequestId);
        db.AutoFetchJobs.Add(new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = cin, Bid = "b" + request.RequestId, Status = AutoFetchJobStatus.Failed,
            FailureReason = "Transient tool timeout.", CreatedUtc = DateTime.UtcNow, AttemptCount = jobAttemptCount
        });
        await db.SaveChangesAsync();

        var adopter = new PipelineAdopter(db, new StaticOptionsMonitor(EnforcingRetries), TimeProvider.System, NullLogger<PipelineAdopter>.Instance);
        var runId = (await adopter.EnsureRunAsync(request.RequestId, PipelineRunTrigger.AutoFetch, null, CancellationToken.None))!.Value;
        return (request.RequestId, runId);
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
    private async Task<(long RequestId, long RunId)> SeedIngestedRequestWithRunAsync(string? cin = "unique", PipelineOptions? adoptOptions = null)
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

        var adopter = new PipelineAdopter(db, new StaticOptionsMonitor(adoptOptions ?? Enforcing), TimeProvider.System, NullLogger<PipelineAdopter>.Instance);
        var runId = (await adopter.EnsureRunAsync(request.RequestId, PipelineRunTrigger.ManualUpload, null, CancellationToken.None))!.Value;
        return (request.RequestId, runId);
    }

    private sealed class RecordingActions(PipelineActionResult? result, Exception? throws = null) : IPipelineActions
    {
        public int Calls { get; private set; }
        public int AnalysisCalls { get; private set; }
        public int DossierCalls { get; private set; }
        public int RetryCalls { get; private set; }
        public DossierRenderResult DossierResult { get; set; } = new(true, false, false, "unused.pdf");

        public Task<PipelineActionResult> StartLitigationSearchAsync(long requestId, string correlationId, CancellationToken ct)
        {
            Calls++;
            return throws is not null ? Task.FromException<PipelineActionResult>(throws) : Task.FromResult(result!);
        }

        public Task<PipelineActionResult> StartLitigationAnalysisAsync(long requestId, string correlationId, CancellationToken ct)
        {
            AnalysisCalls++;
            return throws is not null ? Task.FromException<PipelineActionResult>(throws) : Task.FromResult(result!);
        }

        public Task<DossierRenderResult> EnsureDossierRenderedAsync(long requestId, CancellationToken ct)
        {
            DossierCalls++;
            return Task.FromResult(DossierResult);
        }

        public Task<PipelineActionResult> RetryFetchAsync(long requestId, string correlationId, CancellationToken ct)
        {
            RetryCalls++;
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
