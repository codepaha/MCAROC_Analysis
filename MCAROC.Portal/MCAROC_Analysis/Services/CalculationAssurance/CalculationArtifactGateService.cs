using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>The delivery gate checked at dossier-download time (#164 PR4). A no-op with zero DB queries
/// while CalculationAssurance:Mode is Off. Under Enforced, a report whose current snapshot was never
/// audited at all (no CalculationAuditSnapshot row — e.g. a historical AnalysisRun that completed before
/// this feature shipped, or before Mode was ever anything but Off in this environment) is treated as the
/// maximal case of "NotEvaluated is not a passing audit result" and fails CLOSED, exactly like a confirmed
/// hold. ObserveOnly surfaces the same signal via logging without ever actually blocking — by design, so
/// the "missing snapshot" population can be sized before Enforced is ever proposed for a real
/// environment.</summary>
public class CalculationArtifactGateService(AppDbContext db, IConfiguration config, ILogger<CalculationArtifactGateService> logger)
{
    public async Task<bool> IsHeldAsync(long requestId, long ingestionRunId, long? analysisRunId, DossierVariant variant, CancellationToken ct)
    {
        var mode = CalculationAssuranceConfig.ParseMode(config);
        if (mode == CalculationAssuranceMode.Off)
            return false;

        if (analysisRunId is not { } resolvedAnalysisRunId)
        {
            // Should never happen for a model DossierCache.GetAsync actually returned (it only resolves a
            // model once a completed AnalysisRun exists for the latest completed ingestion) — defensive
            // fail-closed handling if that contract were ever violated.
            logger.LogError(
                "Calculation-assurance gate asked to evaluate request {RequestId} run {IngestionRunId} with no AnalysisRunId.",
                requestId, ingestionRunId);
            return mode == CalculationAssuranceMode.Enforced;
        }

        var snapshotId = await db.CalculationAuditSnapshots
            .Where(s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == resolvedAnalysisRunId)
            .Select(s => (long?)s.CalculationAuditSnapshotId)
            .FirstOrDefaultAsync(ct);

        bool held;
        if (snapshotId is null)
        {
            held = true;
            logger.LogWarning(
                "Calculation-assurance snapshot missing for request {RequestId} run ({IngestionRunId},{AnalysisRunId}) — treated as {Outcome} under {Mode}.",
                requestId, ingestionRunId, resolvedAnalysisRunId, mode == CalculationAssuranceMode.Enforced ? "held" : "would-be-held", mode);
        }
        else
        {
            var variantName = variant.ToString();
            held = await db.CalculationArtifactHolds.AnyAsync(h =>
                h.IsActive && h.CalculationAuditSnapshotId == snapshotId && (h.Variant == null || h.Variant == variantName), ct);
        }

        if (mode == CalculationAssuranceMode.ObserveOnly)
        {
            if (held)
                logger.LogWarning(
                    "ObserveOnly: would have held request {RequestId} run ({IngestionRunId},{AnalysisRunId}) variant {Variant} for download.",
                    requestId, ingestionRunId, resolvedAnalysisRunId, variant);
            return false; // ObserveOnly never actually blocks, by definition
        }

        return held; // Enforced
    }
}
