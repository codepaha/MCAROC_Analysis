using MCAROC_Analysis.Data.Entities;
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
}

public sealed class PipelineActions(LitigationStartService litigation) : IPipelineActions
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
}
