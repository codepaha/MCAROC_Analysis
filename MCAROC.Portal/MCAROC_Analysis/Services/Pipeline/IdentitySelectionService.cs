using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Pipeline;

/// <param name="Resolution">What was recorded for the selection.</param>
/// <param name="RequeuedJobId">The auto-fetch job re-pointed at the selected company and queued, if any.</param>
public sealed record IdentitySelectionResult(IdentityResolutionResult Resolution, long? RequeuedJobId);

/// <summary>A person selects the company for a request whose Resolve stage needs attention — the service behind
/// the board's <b>Select this CIN</b> action (issue #295, plan §5A.3). The caller (an InternalReviewer action)
/// owns authorization, antiforgery and the audit-log row; this records the selection as <c>HumanSelected</c>
/// through <see cref="IdentityResolutionService"/> (same input and hints as the resolution it answers), puts
/// the request's auto-fetch job back to work when a false accept had failed it, logs a pipeline event, and
/// reconciles the run straight away so the stage unblocks on the click rather than on the next tick.
///
/// A job is re-pointed only when it failed before exporting anything: a job that already exported data for
/// another company is never silently reused. A request with no job yet (a name-only intake) gets one from its
/// intake path once identified, not from here.</summary>
public sealed class IdentitySelectionService(
    AppDbContext db, IdentityResolutionService identity, AutoFetchJobService jobs, AutoFetchQueue queue, TimeProvider time,
    ILogger<IdentitySelectionService> logger, PipelineReconciler? reconciler = null)
{
    private static readonly PipelineOutcome[] LiveOutcomes = [PipelineOutcome.InProgress, PipelineOutcome.CoreReady, PipelineOutcome.NeedsAttention];

    public async Task<IdentitySelectionResult> SelectAsync(long requestId, string identifier, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier)) throw new ArgumentException("Select a company.", nameof(identifier));
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("The person selecting must be known.", nameof(actor));

        var (name, hints) = await identity.LatestInputAsync(requestId, ct);
        var resolution = await identity.ApplyHumanSelectionAsync(requestId, identifier, name, hints, actor.Trim(), ct);

        long? requeued = null;
        if (resolution.AppliedToRequest)
            requeued = await RepointFailedJobAsync(requestId, ct);

        var run = await db.PipelineRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId && LiveOutcomes.Contains(r.Outcome))
            .Select(r => new { r.PipelineRunId, r.CorrelationId }).FirstOrDefaultAsync(ct);
        if (run is not null)
        {
            db.PipelineEvents.Add(new PipelineEvent
            {
                PipelineRunId = run.PipelineRunId, Stage = PipelineStage.Resolve, Action = PipelineEventActions.IdentitySelected,
                Actor = actor.Trim(), CorrelationId = run.CorrelationId, AtUtc = time.GetUtcNow().UtcDateTime,
                // Only a selection that could not be applied carries a reason.
                ReasonCode = NotAppliedReason(resolution)
            });
            await db.SaveChangesAsync(ct);

            if (reconciler is not null)
            {
                try { await reconciler.ReconcileAsync(run.PipelineRunId, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The selection is stored; the worker's next tick observes it.
                    logger.LogWarning(ex, "Company selected for request {RequestId}, but reconciling its pipeline run immediately failed", requestId);
                }
            }
        }

        logger.LogInformation("Request {RequestId}: {Actor} selected {Identifier} ({Outcome})", requestId, actor, identifier,
            resolution.AppliedToRequest ? "applied" : NotAppliedReason(resolution));
        return new IdentitySelectionResult(resolution, requeued);
    }

    private async Task<long?> RepointFailedJobAsync(long requestId, CancellationToken ct)
    {
        var job = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .Select(j => new { j.AutoFetchJobId, j.Status, j.Cin, j.RocDocumentId, j.IncludeFilings, j.MaxDocumentsPerSection, j.CorrelationId })
            .FirstOrDefaultAsync(ct);
        if (job is null || job.Status != AutoFetchJobStatus.Failed) return null;
        if (job.RocDocumentId is not null)
        {
            logger.LogWarning("Request {RequestId}: company selected, but its auto-fetch job {JobId} already exported data for {Cin}; not re-pointing it",
                requestId, job.AutoFetchJobId, job.Cin);
            return null;
        }

        var request = await db.Requests.FirstAsync(r => r.RequestId == requestId, ct);
        var reset = await jobs.CreateOrResetJobAsync(request, job.IncludeFilings, job.MaxDocumentsPerSection, ct, job.CorrelationId);
        queue.Enqueue(reset.AutoFetchJobId);
        return reset.AutoFetchJobId;
    }

    private static string? NotAppliedReason(IdentityResolutionResult r) =>
        r.AppliedToRequest ? null
        : r.ExistingRequestId is not null ? ResolutionReasonCodes.DuplicateRequest
        : r.Decision.Status == ResolutionStatus.Resolved ? ResolutionReasonCodes.RequestAlreadyIdentified
        : r.Decision.ReasonCode;
}
