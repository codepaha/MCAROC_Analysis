using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance.Checks;

/// <summary>Reuses CapitalReconciliationMetrics's ledgered "MCA master-data paid-up capital vs standalone
/// share capital" entry as the audited subject, split into two checks: RecomputeIntegrity (a fresh
/// recompute must equal the ledger's stored diff exactly — any mismatch means the ledger/code itself is
/// wrong) and MagnitudeThreshold (a real, tolerated cross-period difference is only worth a human's
/// attention past a configured crore threshold).</summary>
public static class CapitalReconciliationChecks
{
    public const string RecomputeIntegrityCheckKey = "CapitalReconciliation.RecomputeIntegrity";
    public const string MagnitudeThresholdCheckKey = "CapitalReconciliation.MagnitudeThreshold";

    /// <summary>A starting constant for v1 — worth revisiting against real-portfolio data once this
    /// check has run against more than the seed/synthetic fixtures.</summary>
    private const decimal MagnitudeThresholdCrore = 5m;

    public static IReadOnlyList<CalculationCheckOutcome> Run(CalculationCheckContext context)
    {
        var ledgerEntry = context.LedgerEntries.FirstOrDefault(e => e.CalculationKey.StartsWith("CapitalReconciliation.", StringComparison.Ordinal));
        if (ledgerEntry is null)
            return
            [
                CalculationCheckOutcome.NotEvaluated(RecomputeIntegrityCheckKey, "No ledgered capital-reconciliation entry for this snapshot."),
                CalculationCheckOutcome.NotEvaluated(MagnitudeThresholdCheckKey, "No ledgered capital-reconciliation entry for this snapshot.")
            ];

        var relatedIds = new[] { ledgerEntry.CalculationLedgerEntryId };
        var profilePaidUp = context.Model.Corporate.PaidUpCapital;
        var latestShareCapital = context.Model.Financials.Latest?.ShareCapital;

        if (profilePaidUp is null || latestShareCapital is null)
            return
            [
                CalculationCheckOutcome.NotEvaluated(RecomputeIntegrityCheckKey, "Paid-up capital or standalone share capital not on record.", relatedIds),
                CalculationCheckOutcome.NotEvaluated(MagnitudeThresholdCheckKey, "Paid-up capital or standalone share capital not on record.", relatedIds)
            ];

        if (ledgerEntry.ValueNumeric is not { } storedDiff)
            return
            [
                CalculationCheckOutcome.NotEvaluated(RecomputeIntegrityCheckKey, "Ledgered entry has no numeric value to cross-check.", relatedIds),
                CalculationCheckOutcome.NotEvaluated(MagnitudeThresholdCheckKey, "Ledgered entry has no numeric value to cross-check.", relatedIds)
            ];

        // Independent re-derivation — not a call into DossierComputations, so a defect in that shared
        // formula doesn't silently pass its own check.
        var recomputedDiff = profilePaidUp.Value - latestShareCapital.Value;

        var outcomes = new List<CalculationCheckOutcome>(2)
        {
            recomputedDiff != storedDiff
                ? CalculationCheckOutcome.Triggered(RecomputeIntegrityCheckKey, CalculationDiscrepancySeverity.Critical,
                    DetailJson(recomputedDiff, storedDiff), relatedIds)
                : CalculationCheckOutcome.NotTriggered(RecomputeIntegrityCheckKey, relatedIds)
        };

        var magnitude = Math.Abs(recomputedDiff);
        outcomes.Add(magnitude > MagnitudeThresholdCrore
            ? CalculationCheckOutcome.Triggered(MagnitudeThresholdCheckKey, CalculationDiscrepancySeverity.Material,
                DetailJson(recomputedDiff, storedDiff), relatedIds)
            : CalculationCheckOutcome.NotTriggered(MagnitudeThresholdCheckKey, relatedIds));

        return outcomes;
    }

    private static string DetailJson(decimal recomputed, decimal stored) =>
        System.Text.Json.JsonSerializer.Serialize(new { recomputed, stored });
}
