using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>What happened when the coordinator tried to act on a stage.</summary>
/// <param name="Started">The action was taken; <see cref="SourceRef"/> is the job/run it created.</param>
/// <param name="AlreadyExists">Someone else (a reviewer, a racing reconciler) already did it — nothing to record;
/// the next tick observes their job.</param>
/// <param name="ReasonCode">Why it was deferred (stable code, plan §6.1) when neither of the above.</param>
public sealed record PipelineActionResult(bool Started, bool AlreadyExists, long? SourceRef, string? ReasonCode, string? ReasonDetail)
{
    public static PipelineActionResult Deferred(string code, string? detail) => new(false, false, null, code, detail);
}

/// <summary>The actions <c>Enforce</c> mode may take. Each goes through the same idempotent, admission-gated entry
/// point a reviewer's button uses — the coordinator never has a private path to a paid call.</summary>
public interface IPipelineActions
{
    Task<PipelineActionResult> StartLitigationSearchAsync(long requestId, string correlationId, CancellationToken ct);
    Task<PipelineActionResult> StartLitigationAnalysisAsync(long requestId, string correlationId, CancellationToken ct);
    Task<DossierRenderResult> EnsureDossierRenderedAsync(long requestId, CancellationToken ct);
    /// <summary>Plan §6.2: requeue a Failed auto-fetch job — the one coordinator-retryable stage this PR
    /// wires (Analysis/Filings/Dossier retry are a documented fast-follow, not this action's job).</summary>
    Task<PipelineActionResult> RetryFetchAsync(long requestId, string correlationId, CancellationToken ct);
}

public sealed class PipelineActions(LitigationStartService litigation, DossierArtifactService dossier, AutoFetchJobService autoFetch, AutoFetchQueue autoFetchQueue) : IPipelineActions
{
    public async Task<PipelineActionResult> StartLitigationSearchAsync(long requestId, string correlationId, CancellationToken ct)
    {
        var result = await litigation.StartAutoSearchAsync(requestId, correlationId, ct);
        if (result.Started) return new PipelineActionResult(true, false, result.ReferenceId, null, null);
        if (result.ReferenceId is not null) return new PipelineActionResult(false, true, result.ReferenceId, null, null);
        if (result.NotEligible) return PipelineActionResult.Deferred("LITIGATION_NOT_ELIGIBLE", result.Message);
        return PipelineActionResult.Deferred(result.Denial switch
        {
            AdmissionDenial.CostCapReached => "COST_CAP_REACHED",
            AdmissionDenial.Fresh => "LITIGATION_RECENTLY_SEARCHED",
            _ => "LITIGATION_SEARCH_IN_FLIGHT"
        }, result.Denial == AdmissionDenial.Fresh
            ? "Another request searched this company in the last 7 days. Reusing that report isn't built yet — start the search by hand if this request needs its own."
            : result.Message);
    }

    public async Task<PipelineActionResult> StartLitigationAnalysisAsync(long requestId, string correlationId, CancellationToken ct)
    {
        var result = await litigation.StartAutoAnalysisAsync(requestId, correlationId, ct);
        // StartAnalysisAsync's Started=true covers both "created" and "joined an already-active run" — either
        // way nothing was refused, so both map to the coordinator's Started (never AlreadyExists: unlike
        // search, analysis has no pre-existing-job short-circuit before admission is even attempted).
        if (result.Started) return new PipelineActionResult(true, false, result.ReferenceId, null, null);
        if (result.NotEligible) return PipelineActionResult.Deferred("LITIGATION_ANALYSIS_NOT_ELIGIBLE", result.Message);
        return PipelineActionResult.Deferred(result.Denial switch
        {
            AdmissionDenial.CostCapReached => "COST_CAP_REACHED",
            AdmissionDenial.Fresh => "LITIGATION_ANALYSIS_ALREADY_RUN",
            _ => "LITIGATION_ANALYSIS_IN_FLIGHT"
        }, result.Message);
    }

    public Task<DossierRenderResult> EnsureDossierRenderedAsync(long requestId, CancellationToken ct) =>
        dossier.EnsureRenderedAsync(requestId, DossierVariant.Executive, ct);

    public async Task<PipelineActionResult> RetryFetchAsync(long requestId, string correlationId, CancellationToken ct)
    {
        var job = await autoFetch.RequeueAsync(requestId, ct, correlationId);
        if (job is null) return PipelineActionResult.Deferred("FETCH_RETRY_NOT_FOUND", "No auto-fetch job exists for this request.");
        // RequeueAsync is a no-op (returns the job unchanged) when it isn't Failed — e.g. a racing reviewer
        // already retried it by hand, or it moved on since the decision was made. Either way, nothing further
        // to enqueue; the next tick observes whatever it's actually doing now.
        if (job.Status != AutoFetchJobStatus.Queued) return new PipelineActionResult(false, true, job.AutoFetchJobId, null, null);
        autoFetchQueue.Enqueue(job.AutoFetchJobId);
        return new PipelineActionResult(true, false, job.AutoFetchJobId, null, null);
    }
}
