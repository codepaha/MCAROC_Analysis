using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
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
    IOptionsMonitor<PipelineOptions>? options = null, IPipelineActions? actions = null)
{
    private static readonly string LeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);
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
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now2.Add(LeaseDuration)), ct);
        if (fenced == 0)
        {
            await tx.RollbackAsync(ct);
            logger.LogInformation("Pipeline run {RunId} lease was taken over; discarding this reconcile.", runId);
            return false;
        }

        var opts = options?.CurrentValue;
        var enforceLitigation = actions is not null && opts is not null && opts.EnforcesLitigationSearch();
        var enforceAnalysis = actions is not null && opts is not null && opts.EnforcesLitigationAnalysis();
        var enforceDossier = actions is not null && opts is not null && opts.EnforcesDossierPreRender();
        var enforceRetries = actions is not null && opts is not null && opts.EnforcesRetries();
        var toStart = new List<PipelineStage>();
        var toRetry = new List<PipelineStage>();
        var preRenderDossier = false;
        var existing = await db.PipelineStageStates.Where(s => s.PipelineRunId == runId).ToDictionaryAsync(s => s.Stage, ct);
        foreach (var (stage, verdict0) in decision.Stages)
        {
            var verdict = verdict0;
            // Plan §6.2: only Fetch is wired to a coordinator-driven retry today (#292's first slice —
            // Analysis/Filings/Dossier retry are a documented fast-follow, not a design decision that they
            // never should be). Paid stages (Litigation/LitigationAnalysis) are deliberately never in this
            // set — a failure there after a purchase needs a human, never an automatic re-buy.
            if (stage == PipelineStage.Fetch && verdict.State == PipelineStageStateKind.NeedsAttention
                && opts is not null && PipelineFailureClassifier.IsAutoRetryable(verdict.ReasonCode))
            {
                existing.TryGetValue(stage, out var retryRow);
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
            }
            else if (row.State == verdict.State && row.SkipKind == verdict.SkipKind && row.ReasonCode == reasonCode
                     && row.ReasonDetail == reasonDetail && row.SourceRef == verdict.SourceRef)
            {
                continue;
            }

            var stateChanged = row.State != verdict.State || db.Entry(row).State == EntityState.Added;
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

        var coreReadyUtc = run.CoreReadyUtc ?? (decision.CoreReady ? now2 : null);
        DateTime? completedUtc = decision.Outcome is PipelineOutcome.Complete or PipelineOutcome.CompleteWithWarnings or PipelineOutcome.Cancelled ? now2 : null;
        await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, decision.Outcome)
                .SetProperty(r => r.CoreReadyUtc, coreReadyUtc)
                .SetProperty(r => r.CompletedUtc, completedUtc), ct);
        await tx.CommitAsync(ct);

        foreach (var stage in toStart)
            await StartAsync(stage, runId, token, run.RequestId, run.CorrelationId, opts!, ct);
        foreach (var stage in toRetry)
            await RetryAsync(stage, runId, token, run.RequestId, run.CorrelationId, ct);
        if (preRenderDossier)
            await PreRenderDossierAsync(runId, token, run.RequestId, run.CorrelationId, ct);
        await ReleaseAsync(runId, token, ct);

        if (decision.Outcome != run.Outcome)
            logger.LogInformation("Pipeline run {RunId} (request {RequestId}): {Old} -> {New}", runId, run.RequestId, run.Outcome, decision.Outcome);
        return true;
    }

    private static bool IsReadyToStart(StageVerdict verdict) =>
        verdict.State == PipelineStageStateKind.NotStarted && verdict.ReasonCode == PipelineDecider.ReadyToStart;

    /// <summary>Plan §6.5: counts distinct runs currently actively searching or analysing litigation —
    /// across every request, not just this one — so a burst of requests all becoming ready at once can't
    /// all start together and overrun BPR's/Vertex's own limits. A manual start (never routed through
    /// <see cref="StartAsync"/>) is deliberately not counted against or bound by this cap.</summary>
    private async Task<bool> ConcurrencyCapReachedAsync(int maxConcurrentRuns, CancellationToken ct)
    {
        var active = await db.PipelineStageStates.AsNoTracking()
            .Where(s => (s.Stage == PipelineStage.Litigation || s.Stage == PipelineStage.LitigationAnalysis) && s.State == PipelineStageStateKind.Running)
            .Select(s => s.PipelineRunId).Distinct().CountAsync(ct);
        return active >= maxConcurrentRuns;
    }

    private async Task StartAsync(PipelineStage stage, long runId, Guid token, long requestId, string correlationId, PipelineOptions opts, CancellationToken ct)
    {
        PipelineActionResult result;
        if (opts.MaxConcurrentRuns > 0 && await ConcurrencyCapReachedAsync(opts.MaxConcurrentRuns, ct))
        {
            result = PipelineActionResult.Deferred("CONCURRENCY_CAP_REACHED",
                $"At the coordinator's concurrent-enrichment cap ({opts.MaxConcurrentRuns}); will retry shortly.");
        }
        else
        {
            try
            {
                result = stage == PipelineStage.Litigation
                    ? await actions!.StartLitigationSearchAsync(requestId, correlationId, ct)
                    : await actions!.StartLitigationAnalysisAsync(requestId, correlationId, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Automatic {Stage} start failed for request {RequestId}", stage, requestId);
                db.ChangeTracker.Clear();
                result = PipelineActionResult.Deferred("AUTO_START_FAILED", ex.Message);
            }
        }
        if (result.AlreadyExists) return; // the next tick observes that job

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
