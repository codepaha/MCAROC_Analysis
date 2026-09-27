using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Reconciles one run: read the facts, decide, persist stage states/events/outcome — and, in
/// <c>Enforce</c> mode only, take the one action the decision calls for (today: start the litigation search).
/// Observe mode writes only to <c>PipelineRuns</c>, <c>PipelineStageStates</c> and <c>PipelineEvents</c>.
///
/// The action runs after the state commit but while the run's lease is still held, so no second reconciler can
/// act on the same run meanwhile; the action's own entry point is idempotent and admission-gated regardless. A
/// refused start is recorded on the stage (<c>NextAttemptUtc</c>, reason) and not retried before that time.
///
/// Concurrency: a run is claimed with a fenced lease (single conditional UPDATE, fresh token per claim), and
/// the persisting transaction starts by re-asserting that token on the run row. That UPDATE also holds the
/// row's lock until commit, so a second reconciler can neither claim the run mid-write nor publish after its
/// own lease was taken over.
///
/// <c>ReconcileLeaseExpiresUtc</c> means "not claimable before": the lease expiry while held, and
/// <see cref="MinReconcileInterval"/> past the release once done. Without that interval a competitor that
/// lost the race only by being slow would find the lease already released and reconcile the run a second time
/// straight away; with it, exactly one of any set of racing reconcilers processes a run, and the worker (whose
/// tick is far longer) still picks it up again on its next tick.</summary>
public sealed class PipelineReconciler(AppDbContext db, PipelineSnapshotReader reader, TimeProvider time, ILogger<PipelineReconciler> logger,
    IOptionsMonitor<PipelineOptions>? options = null, IPipelineActions? actions = null, IOperationalSlotLeaseService? slotLeases = null)
{
    private static readonly string LeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    /// <summary>Plan §6.5's concurrency cap, atomically enforced (PR #311 review: a plain count-then-start
    /// races across reconciler instances) via the same sp_getapplock-fenced multi-holder lease
    /// <see cref="OperationalSlotLeaseService"/> already uses for LargeUpload/LargeUnpack — two concurrent
    /// acquire attempts for this slot type serialize against each other, so one always sees the other's
    /// commit before deciding. Litigation and LitigationAnalysis share one pool (both draw on the same pair
    /// of external dependencies the cap protects). A generous fixed duration is the self-healing fallback if
    /// the explicit release below (on the stage actually leaving Running) is ever missed — same fail-safe
    /// shape as every other <c>OperationalSlotLease</c> holder, never the primary path.</summary>
    internal const string LitigationEnrichmentSlot = "PipelineLitigationEnrichment";
    // PR #311 second review: a fixed duration with no renewal meant genuinely long-running work would
    // eventually get reaped by TryAcquireSlotAsync's own opportunistic expired-lease cleanup and let another
    // start over the cap. Renewed every tick a stage is observed Running (below), so this only needs to
    // outlast the gap BETWEEN ticks, not the stage's total runtime — a lease-based mechanism can only ever
    // guarantee correctness as long as its holder keeps renewing before expiry (same tradeoff every other
    // lease in this codebase already accepts: PipelineRun's own 2-minute reconcile lease, the domain-level
    // litigation leases). Missing every renewal for this long straight would mean the coordinator itself has
    // stopped ticking this run entirely, at which point cap correctness is the least of the problems.
    internal static readonly TimeSpan LitigationEnrichmentSlotDuration = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MinReconcileInterval = TimeSpan.FromSeconds(5);

    private static readonly PipelineOutcome[] LiveOutcomes = [PipelineOutcome.InProgress, PipelineOutcome.CoreReady, PipelineOutcome.NeedsAttention];

    /// <summary>Live runs that are claimable now, least-recently-reconciled first (the release time is kept
    /// in the expiry column, so this rotates through every run).</summary>
    public Task<List<long>> SelectDueRunIdsAsync(int max, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return db.PipelineRuns.AsNoTracking()
            .Where(r => LiveOutcomes.Contains(r.Outcome) && (r.ReconcileLeaseExpiresUtc == null || r.ReconcileLeaseExpiresUtc < now))
            .OrderBy(r => r.ReconcileLeaseExpiresUtc).ThenBy(r => r.PipelineRunId)
            .Select(r => r.PipelineRunId)
            .Take(max)
            .ToListAsync(ct);
    }

    /// <summary>Returns false when another reconciler holds (or took over) the run.</summary>
    public async Task<bool> ReconcileAsync(long runId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var token = Guid.NewGuid();
        var claimed = await db.PipelineRuns
            .Where(r => r.PipelineRunId == runId && LiveOutcomes.Contains(r.Outcome)
                && (r.ReconcileLeaseExpiresUtc == null || r.ReconcileLeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ReconcileLeaseOwner, LeaseOwner)
                .SetProperty(r => r.ReconcileLeaseToken, token)
                .SetProperty(r => r.ReconcileLeaseExpiresUtc, now.Add(LeaseDuration)), ct);
        if (claimed == 0) return false;

        var run = await db.PipelineRuns.AsNoTracking().Where(r => r.PipelineRunId == runId)
            .Select(r => new { r.RequestId, r.PolicyJson, r.CorrelationId, r.CoreReadyUtc, r.Outcome }).SingleAsync(ct);
        var snapshot = await reader.ReadAsync(run.RequestId, ct);
        if (snapshot is null)
        {
            await ReleaseAsync(runId, token, ct);
            return true;
        }

        var now2 = time.GetUtcNow().UtcDateTime;
        var decision = PipelineDecider.Decide(snapshot, ParsePolicy(run.PolicyJson), now2, options?.CurrentValue.Stall);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token && r.Outcome != PipelineOutcome.Cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now2.Add(LeaseDuration)), ct);
        if (fenced == 0)
        {
            await tx.RollbackAsync(ct);
            logger.LogInformation("Pipeline run {RunId} lease was taken over or run was cancelled; discarding this reconcile.", runId);
            return false;
        }

        var requestCancelled = await db.Requests.AsNoTracking().Where(r => r.RequestId == run.RequestId)
            .Select(r => r.RequestStatus == RequestStatus.Cancelled).FirstOrDefaultAsync(ct);
        if (requestCancelled)
        {
            await tx.RollbackAsync(ct);
            logger.LogInformation("Pipeline run {RunId} request is cancelled; discarding this reconcile.", runId);
            return false;
        }

        var opts = options?.CurrentValue;
        var enforceLitigation = actions is not null && opts is not null && opts.EnforcesLitigationSearch();
        var enforceAnalysis = actions is not null && opts is not null && opts.EnforcesLitigationAnalysis();
        var enforceDossier = actions is not null && opts is not null && opts.EnforcesDossierPreRender();
        var enforceRetries = actions is not null && opts is not null && opts.EnforcesRetries();
        var toStart = new List<PipelineStage>();
        var toRetry = new List<PipelineStage>();
        var toReleaseSlots = new List<PipelineStage>();
        var toRenewSlots = new List<PipelineStage>();
        var preRenderDossier = false;
        var existing = await db.PipelineStageStates.Where(s => s.PipelineRunId == runId).ToDictionaryAsync(s => s.Stage, ct);
        foreach (var (stage, verdict0) in decision.Stages)
        {
            var verdict = verdict0;
            existing.TryGetValue(stage, out var existingRow);

            var isEnrichmentStage = !PipelineOutcomeCalculator.CoreStages.Contains(stage);
            if (isEnrichmentStage && existingRow?.State == PipelineStageStateKind.Skipped && existingRow.ReasonCode == PipelineEventActions.ManualSkippedCode
                && verdict0.State is not (PipelineStageStateKind.Succeeded or PipelineStageStateKind.SucceededWithWarnings or PipelineStageStateKind.Running))
            {
                verdict = new StageVerdict(PipelineStageStateKind.Skipped, PipelineStageSkipKind.Warning, PipelineEventActions.ManualSkippedCode, existingRow.ReasonDetail, existingRow.SourceRef);
            }
            else if (existingRow?.State == PipelineStageStateKind.Cancelled && existingRow.ReasonCode == PipelineEventActions.CancelledCode
                && (run.Outcome == PipelineOutcome.Cancelled || snapshot.RequestStatus == RequestStatus.Cancelled))
            {
                verdict = new StageVerdict(PipelineStageStateKind.Cancelled, null, PipelineEventActions.CancelledCode, existingRow.ReasonDetail, existingRow.SourceRef);
            }

            // Evaluated unconditionally (not just on a state change) so a stage that stays Running tick after
            // tick — the common case for a long-running search/analysis — still gets here even when the
            // per-stage row-upsert below takes its "nothing changed" early exit. A holder id that was never
            // actually reserved (a manually-started search) renews nothing — TryRenewSlotAsync is a no-op
            // when it finds no matching row, same as ReleaseSlotAsync.
            if (stage is PipelineStage.Litigation or PipelineStage.LitigationAnalysis && verdict.State == PipelineStageStateKind.Running)
                toRenewSlots.Add(stage);
            // Plan §6.2: only Fetch is wired to a coordinator-driven retry today (#292's first slice —
            // Analysis/Filings/Dossier retry are a documented fast-follow, not a design decision that they
            // never should be). Paid stages (Litigation/LitigationAnalysis) are deliberately never in this
            // set — a failure there after a purchase needs a human, never an automatic re-buy.
            if (stage == PipelineStage.Fetch && verdict.State == PipelineStageStateKind.NeedsAttention
                && opts is not null && PipelineFailureClassifier.IsAutoRetryable(verdict.ReasonCode))
            {
                var retryRow = existingRow;
                // The fetch worker has no internal retry loop of its own (one claim = one AttemptCount++,
                // terminal either way) — but it IS claimed again by a manual UI retry
                // (AutoFetchController.RetryJob) without ever going through this coordinator, so
                // AutoFetchJob.AttemptCount can run ahead of PipelineStageState.Attempts. Plan §6.2: internal
                // attempts count toward the same cap, so the worst case stays bounded regardless of who
                // triggered them. Max, not sum: a coordinator-driven retry advances both counters for the
                // same attempt, and summing would double-count it.
                var jobAttemptCount = await db.AutoFetchJobs.AsNoTracking()
                    .Where(j => j.RequestId == run.RequestId).Select(j => j.AttemptCount).FirstOrDefaultAsync(ct);
                var attempts = Math.Max(retryRow?.Attempts ?? 0, jobAttemptCount);
                if (attempts >= opts.MaxCoordinatorAttempts)
                {
                    // Re-applied every tick (not just once): the decider keeps reporting the underlying
                    // FETCH_FAILED forever, so without this the exhausted state would flip back and forth
                    // with the raw reason every other tick. Idempotent — once the row already reads
                    // RETRIES_EXHAUSTED, this produces the exact same verdict and the unchanged-row check
                    // below skips it like any other no-op tick.
                    verdict = verdict with { ReasonCode = "RETRIES_EXHAUSTED", ReasonDetail = verdict.ReasonDetail };
                }
                else if (enforceRetries)
                {
                    // No row yet (this is the first tick this stage has ever been NeedsAttention+Transient):
                    // fall through to the normal row-upsert below so RetryAsync has a row to find afterwards
                    // — mirroring exactly how the Litigation/LitigationAnalysis start below handles its own
                    // first-ever "ready to start" tick. Only once a row already exists does its own
                    // NextAttemptUtc gate a repeat, and only then is skipping the row-upsert (via continue)
                    // safe, because there is nothing new for it to record this tick.
                    if (retryRow is null)
                        toRetry.Add(stage);
                    else if (retryRow.NextAttemptUtc is null || retryRow.NextAttemptUtc <= now2)
                    {
                        toRetry.Add(stage);
                        continue;
                    }
                }
            }

            if (stage is PipelineStage.Litigation or PipelineStage.LitigationAnalysis && IsReadyToStart(verdict))
            {
                var enforce = stage == PipelineStage.Litigation ? enforceLitigation : enforceAnalysis;
                // A refused automatic start keeps its reason on the row until it is due again, and while it is
                // retried — so a repeat of the same refusal isn't logged as a new step.
                if (existing.TryGetValue(stage, out var deferred) && deferred.NextAttemptUtc is { } due)
                {
                    if (due > now2) continue;
                    if (enforce)
                    {
                        toStart.Add(stage);
                        continue;
                    }
                }
                else if (enforce) toStart.Add(stage);
            }

            var reasonCode = Truncate(verdict.ReasonCode, 60);
            var reasonDetail = Truncate(verdict.ReasonDetail, 1000);
            if (!existing.TryGetValue(stage, out var row))
            {
                row = new PipelineStageState { PipelineRunId = runId, Stage = stage };
                db.PipelineStageStates.Add(row);
                existing[stage] = row;
            }
            else if (row.State == verdict.State && row.SkipKind == verdict.SkipKind && row.ReasonCode == reasonCode
                     && row.ReasonDetail == reasonDetail && row.SourceRef == verdict.SourceRef)
            {
                continue;
            }

            var stateChanged = row.State != verdict.State || db.Entry(row).State == EntityState.Added;
            // The matching half of StartAsync's reservation: once a litigation stage that was actively
            // Running leaves that state (succeeds, fails, needs attention — anything), whatever slot it may
            // have held is released. Harmless to call for a manually-started run that never held one —
            // ReleaseSlotAsync is a delete-where-matching, a no-op when nothing matches.
            if (stage is PipelineStage.Litigation or PipelineStage.LitigationAnalysis
                && row.State == PipelineStageStateKind.Running && verdict.State != PipelineStageStateKind.Running)
                toReleaseSlots.Add(stage);
            row.State = verdict.State;
            row.SkipKind = verdict.SkipKind;
            row.ReasonCode = reasonCode;
            row.ReasonDetail = reasonDetail;
            row.SourceRef = verdict.SourceRef;
            row.UpdatedUtc = now2;
            if (row.StartedUtc is null && verdict.State is not (PipelineStageStateKind.NotStarted or PipelineStageStateKind.Waiting))
                row.StartedUtc = now2;

            if (stateChanged)
                db.PipelineEvents.Add(new PipelineEvent
                {
                    PipelineRunId = runId, Stage = stage, Action = PipelineEventActions.Observed(verdict.State), Actor = "system",
                    ReasonCode = reasonCode, CorrelationId = run.CorrelationId, AtUtc = now2
                });

            // Trigger only on the tick Dossier first becomes done — a later rerun that changes SourceRef
            // without the State enum value itself changing (e.g. a new analysis after re-ingestion) falls
            // back to the controller's own on-demand render instead; this is a latency optimisation, not a
            // correctness requirement, so missing that case is an accepted, documented gap (plan §4.3).
            if (stage == PipelineStage.Dossier && enforceDossier && verdict.IsDone && stateChanged)
                preRenderDossier = true;
        }
        await db.SaveChangesAsync(ct);

        var finalStates = decision.Stages.Keys.ToDictionary(k => k, k => existing.TryGetValue(k, out var r) ? r.State : decision.Stages[k].State);
        var finalSkipKinds = decision.Stages.Keys.ToDictionary(k => k, k => existing.TryGetValue(k, out var r) ? r.SkipKind : decision.Stages[k].SkipKind);
        var finalCancelled = snapshot.RequestStatus == RequestStatus.Cancelled || run.Outcome == PipelineOutcome.Cancelled;
        var finalOutcome = PipelineOutcomeCalculator.Aggregate(finalStates, finalSkipKinds, finalCancelled);
        var finalCoreReady = !finalCancelled && PipelineOutcomeCalculator.IsCoreReady(finalStates, finalSkipKinds);

        var coreReadyUtc = run.CoreReadyUtc ?? (finalCoreReady ? now2 : null);
        DateTime? completedUtc = finalOutcome is PipelineOutcome.Complete or PipelineOutcome.CompleteWithWarnings or PipelineOutcome.Cancelled ? now2 : null;
        var updated = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token && r.Outcome != PipelineOutcome.Cancelled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, finalOutcome)
                .SetProperty(r => r.CoreReadyUtc, coreReadyUtc)
                .SetProperty(r => r.CompletedUtc, completedUtc), ct);
        if (updated == 0)
        {
            await tx.RollbackAsync(ct);
            logger.LogInformation("Pipeline run {RunId} lease fence lost or run was cancelled before commit; discarding this reconcile.", runId);
            return false;
        }
        await tx.CommitAsync(ct);

        if (finalCancelled)
        {
            toStart.Clear();
            toRetry.Clear();
            preRenderDossier = false;
        }

        foreach (var stage in toStart)
            await StartAsync(stage, runId, token, run.RequestId, run.CorrelationId, opts!, ct);
        foreach (var stage in toRetry)
            await RetryAsync(stage, runId, token, run.RequestId, run.CorrelationId, ct);
        if (slotLeases is not null)
        {
            foreach (var stage in toRenewSlots)
                await slotLeases.TryRenewSlotAsync(LitigationEnrichmentSlot, EnrichmentSlotHolderId(runId, stage), LitigationEnrichmentSlotDuration, ct);
            foreach (var stage in toReleaseSlots)
                await slotLeases.ReleaseSlotAsync(LitigationEnrichmentSlot, EnrichmentSlotHolderId(runId, stage), ct);
        }
        if (preRenderDossier)
            await PreRenderDossierAsync(runId, token, run.RequestId, run.CorrelationId, ct);
        await ReleaseAsync(runId, token, ct);

        if (finalOutcome != run.Outcome)
            logger.LogInformation("Pipeline run {RunId} (request {RequestId}): {Old} -> {New}", runId, run.RequestId, run.Outcome, finalOutcome);
        return true;
    }

    private static bool IsReadyToStart(StageVerdict verdict) =>
        verdict.State == PipelineStageStateKind.NotStarted && verdict.ReasonCode == PipelineDecider.ReadyToStart;

    /// <summary>The lease holder id for one run's occupancy of the shared litigation-enrichment slot — one
    /// row per (run, stage), so Litigation and LitigationAnalysis on the same run each hold their own slot
    /// rather than being conflated into one.</summary>
    internal static string EnrichmentSlotHolderId(long runId, PipelineStage stage) => $"{runId}:{stage}";

    private async Task StartAsync(PipelineStage stage, long runId, Guid token, long requestId, string correlationId, PipelineOptions opts, CancellationToken ct)
    {
        PipelineActionResult result;
        var holderId = EnrichmentSlotHolderId(runId, stage);
        var slotAcquired = false;
        if (opts.MaxConcurrentRuns > 0)
        {
            // Reserve BEFORE calling the real action, and only release again immediately below if the start
            // didn't actually happen — the reservation must exist for the whole time real work might be
            // running, not just for the instant of starting it, or the cap would only ever bound concurrent
            // *start attempts* rather than concurrent *enrichment*, which is what plan §6.5 asks for. The
            // matching release for a start that DID succeed lives in ReconcileAsync's own stage loop, fired
            // when the stage is later observed leaving Running — a separate reconcile, possibly a separate
            // reconciler instance, which is exactly why this can't be a plain SELECT COUNT then start (PR
            // #311 review): two concurrent StartAsync calls would both read "under cap" before either's
            // start is visible to the other. sp_getapplock inside TryAcquireSlotAsync fences that race.
            if (slotLeases is null)
            {
                // No fail-open here: without the real mechanism to enforce it, the only safe answer to a
                // configured cap is to defer, never to silently let concurrency go unbounded.
                result = PipelineActionResult.Deferred("CONCURRENCY_CAP_REACHED",
                    "Concurrency limiter is not available; deferring to stay within the configured cap.");
            }
            else
            {
                var lease = await slotLeases.TryAcquireSlotAsync(LitigationEnrichmentSlot, holderId, LitigationEnrichmentSlotDuration, opts.MaxConcurrentRuns, ct);
                if (!lease.Success)
                {
                    result = PipelineActionResult.Deferred("CONCURRENCY_CAP_REACHED",
                        $"At the coordinator's concurrent-enrichment cap ({opts.MaxConcurrentRuns}); will retry shortly.");
                }
                else
                {
                    slotAcquired = true;
                    result = await InvokeStartActionAsync(stage, requestId, correlationId, ct);
                    if (!result.Started)
                    {
                        await slotLeases.ReleaseSlotAsync(LitigationEnrichmentSlot, holderId, ct);
                        slotAcquired = false;
                    }
                }
            }
        }
        else
        {
            result = await InvokeStartActionAsync(stage, requestId, correlationId, ct);
        }
        if (result.AlreadyExists)
        {
            if (slotAcquired) await slotLeases!.ReleaseSlotAsync(LitigationEnrichmentSlot, holderId, ct);
            return; // the next tick observes that job
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now.Add(LeaseDuration)), ct);
        var row = await db.PipelineStageStates.SingleOrDefaultAsync(s => s.PipelineRunId == runId && s.Stage == stage, ct);
        var previousCode = row?.ReasonCode;
        if (fenced == 1 && row is not null)
        {
            row.Attempts++;
            row.UpdatedUtc = now;
            if (result.Started)
            {
                row.State = PipelineStageStateKind.Running;
                row.ReasonCode = PipelineEventActions.AutoStartedCode;
                row.ReasonDetail = null;
                row.SourceRef = result.SourceRef;
                row.NextAttemptUtc = null;
                row.StartedUtc ??= now;
            }
            else
            {
                row.ReasonCode = Truncate(result.ReasonCode, 60);
                row.ReasonDetail = Truncate(result.ReasonDetail, 1000);
                row.NextAttemptUtc = now.AddMinutes(Math.Max(1, opts.AutoStartRetryMinutes));
            }
        }
        // The start itself is a fact whether or not the lease survived; a repeated refusal for the same reason is not news.
        if (result.Started || result.ReasonCode != previousCode)
            db.PipelineEvents.Add(new PipelineEvent
            {
                PipelineRunId = runId, Stage = stage, Actor = "system", CorrelationId = correlationId, AtUtc = now,
                Action = result.Started ? PipelineEventActions.AutoStarted : PipelineEventActions.AutoStartDeferred,
                ReasonCode = result.Started ? null : Truncate(result.ReasonCode, 60)
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (result.Started)
            logger.LogInformation("Pipeline run {RunId}: started {Stage} {JobId} for request {RequestId} automatically", runId, stage, result.SourceRef, requestId);
        else
            logger.LogInformation("Pipeline run {RunId}: automatic {Stage} start for request {RequestId} deferred ({Code}): {Detail}", runId, stage, requestId, result.ReasonCode, result.ReasonDetail);
    }

    private async Task<PipelineActionResult> InvokeStartActionAsync(PipelineStage stage, long requestId, string correlationId, CancellationToken ct)
    {
        try
        {
            return stage == PipelineStage.Litigation
                ? await actions!.StartLitigationSearchAsync(requestId, correlationId, ct)
                : await actions!.StartLitigationAnalysisAsync(requestId, correlationId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Automatic {Stage} start failed for request {RequestId}", stage, requestId);
            db.ChangeTracker.Clear();
            return PipelineActionResult.Deferred("AUTO_START_FAILED", ex.Message);
        }
    }

    /// <summary>Plan §6.2. Unlike <see cref="StartAsync"/>, both outcomes set <c>NextAttemptUtc</c> from
    /// <see cref="PipelineBackoff"/> keyed on the incremented attempt count — a retry that starts successfully
    /// but fails again quickly must still wait out the schedule before the next one, not fire on the very
    /// next tick just because nothing refused it this time.</summary>
    private async Task RetryAsync(PipelineStage stage, long runId, Guid token, long requestId, string correlationId, CancellationToken ct)
    {
        PipelineActionResult result;
        try
        {
            result = await actions!.RetryFetchAsync(requestId, correlationId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Automatic {Stage} retry failed for request {RequestId}", stage, requestId);
            db.ChangeTracker.Clear();
            result = PipelineActionResult.Deferred("RETRY_FAILED", ex.Message);
        }
        if (result.AlreadyExists) return; // moved on since the decision was made — the next tick observes it

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now.Add(LeaseDuration)), ct);
        var row = await db.PipelineStageStates.SingleOrDefaultAsync(s => s.PipelineRunId == runId && s.Stage == stage, ct);
        var previousCode = row?.ReasonCode;
        if (fenced == 1 && row is not null)
        {
            row.Attempts++;
            row.UpdatedUtc = now;
            row.NextAttemptUtc = now.Add(PipelineBackoff.NextDelay(row.Attempts));
            if (result.Started)
            {
                row.State = PipelineStageStateKind.Running;
                row.ReasonCode = PipelineEventActions.AutoRetriedCode;
                row.ReasonDetail = null;
                row.StartedUtc ??= now;
            }
            else
            {
                row.ReasonCode = Truncate(result.ReasonCode, 60);
                row.ReasonDetail = Truncate(result.ReasonDetail, 1000);
            }
        }
        if (result.Started || result.ReasonCode != previousCode)
            db.PipelineEvents.Add(new PipelineEvent
            {
                PipelineRunId = runId, Stage = stage, Actor = "system", CorrelationId = correlationId, AtUtc = now,
                Action = result.Started ? PipelineEventActions.AutoRetried : PipelineEventActions.AutoRetryDeferred,
                ReasonCode = result.Started ? null : Truncate(result.ReasonCode, 60)
            });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (result.Started)
            logger.LogInformation("Pipeline run {RunId}: retried {Stage} for request {RequestId} automatically ({Attempts} coordinator attempt(s) so far).", runId, stage, requestId, row?.Attempts);
        else
            logger.LogInformation("Pipeline run {RunId}: automatic {Stage} retry for request {RequestId} deferred ({Code}): {Detail}", runId, stage, requestId, result.ReasonCode, result.ReasonDetail);
    }

    /// <summary>Plan §4.3. Best-effort: a failure here is logged and left for the controller's own on-demand
    /// render to cover — it never touches the Dossier stage's row (already correctly Succeeded from the
    /// decision above) and it is never retried by this coordinator before the state changes again. Full
    /// retry/backoff for a genuine render failure is #292's job, not this optimisation's.</summary>
    private async Task PreRenderDossierAsync(long runId, Guid token, long requestId, string correlationId, CancellationToken ct)
    {
        DossierRenderResult result;
        try
        {
            result = await actions!.EnsureDossierRenderedAsync(requestId, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Automatic dossier pre-render failed for request {RequestId}; the next download renders on demand instead.", requestId);
            return;
        }
        if (!result.Rendered) return; // not ready / held — nothing to log, the Dossier stage row already says why

        var now = time.GetUtcNow().UtcDateTime;
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now.Add(LeaseDuration)), ct);
        if (fenced == 1)
        {
            db.PipelineEvents.Add(new PipelineEvent
            {
                PipelineRunId = runId, Stage = PipelineStage.Dossier, Actor = "system", CorrelationId = correlationId, AtUtc = now,
                Action = PipelineEventActions.DossierPreRendered
            });
            await db.SaveChangesAsync(ct);
        }
        logger.LogInformation("Pipeline run {RunId}: pre-rendered the dossier for request {RequestId}.", runId, requestId);
    }

    private Task ReleaseAsync(long runId, Guid token, CancellationToken ct) =>
        db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ReconcileLeaseOwner, (string?)null)
                .SetProperty(r => r.ReconcileLeaseToken, (Guid?)null)
                .SetProperty(r => r.ReconcileLeaseExpiresUtc, time.GetUtcNow().UtcDateTime.Add(MinReconcileInterval)), ct);

    public static PipelinePolicy ParsePolicy(string policyJson)
    {
        try { return JsonSerializer.Deserialize<PipelinePolicy>(policyJson) ?? new PipelinePolicy(); }
        catch (JsonException) { return new PipelinePolicy(); }
    }

    private static string? Truncate(string? value, int max) => value is { Length: var n } && n > max ? value[..max] : value;
}
