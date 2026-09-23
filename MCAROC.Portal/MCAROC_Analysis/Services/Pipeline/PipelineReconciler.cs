using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Reconciles one run: read the facts, decide, persist stage states/events/outcome. Writes only to
/// <c>PipelineRuns</c>, <c>PipelineStageStates</c> and <c>PipelineEvents</c> — Observe mode takes no action
/// anywhere else.
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
public sealed class PipelineReconciler(AppDbContext db, PipelineSnapshotReader reader, TimeProvider time, ILogger<PipelineReconciler> logger)
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

        var decision = PipelineDecider.Decide(snapshot, ParsePolicy(run.PolicyJson));

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var now2 = time.GetUtcNow().UtcDateTime;
        var fenced = await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReconcileLeaseExpiresUtc, now2.Add(LeaseDuration)), ct);
        if (fenced == 0)
        {
            await tx.RollbackAsync(ct);
            logger.LogInformation("Pipeline run {RunId} lease was taken over; discarding this reconcile.", runId);
            return false;
        }

        var existing = await db.PipelineStageStates.Where(s => s.PipelineRunId == runId).ToDictionaryAsync(s => s.Stage, ct);
        foreach (var (stage, verdict) in decision.Stages)
        {
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
                    PipelineRunId = runId, Stage = stage, Action = $"Observed:{verdict.State}", Actor = "system",
                    ReasonCode = reasonCode, CorrelationId = run.CorrelationId, AtUtc = now2
                });
        }
        await db.SaveChangesAsync(ct);

        var coreReadyUtc = run.CoreReadyUtc ?? (decision.CoreReady ? now2 : null);
        DateTime? completedUtc = decision.Outcome is PipelineOutcome.Complete or PipelineOutcome.CompleteWithWarnings or PipelineOutcome.Cancelled ? now2 : null;
        await db.PipelineRuns.Where(r => r.PipelineRunId == runId && r.ReconcileLeaseToken == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Outcome, decision.Outcome)
                .SetProperty(r => r.CoreReadyUtc, coreReadyUtc)
                .SetProperty(r => r.CompletedUtc, completedUtc)
                .SetProperty(r => r.ReconcileLeaseOwner, (string?)null)
                .SetProperty(r => r.ReconcileLeaseToken, (Guid?)null)
                .SetProperty(r => r.ReconcileLeaseExpiresUtc, now2.Add(MinReconcileInterval)), ct);
        await tx.CommitAsync(ct);

        if (decision.Outcome != run.Outcome)
            logger.LogInformation("Pipeline run {RunId} (request {RequestId}): {Old} -> {New}", runId, run.RequestId, run.Outcome, decision.Outcome);
        return true;
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
