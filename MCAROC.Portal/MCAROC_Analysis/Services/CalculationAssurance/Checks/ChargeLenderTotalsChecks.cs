using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance.Checks;

/// <summary>Sums open-charge CurrentAmount per lender and asserts it equals the grand total — a
/// structural sum-of-parts invariant. Under correct code this is mathematically guaranteed and should
/// almost always be NotTriggered; a mismatch would mean real corruption (double-counting, a dropped
/// charge), which is exactly the kind of bank-facing credit-risk fact this check exists to catch, not a
/// display nuance.</summary>
public static class ChargeLenderTotalsChecks
{
    public const string LenderTotalsIntegrityCheckKey = "ChargeRegister.LenderTotalsIntegrity";

    public static IReadOnlyList<CalculationCheckOutcome> Run(CalculationCheckContext context)
    {
        var chargeEntries = context.LedgerEntries
            .Where(e => e.CalculationKey.StartsWith("ChargeRegister.", StringComparison.Ordinal))
            .ToList();

        if (chargeEntries.Count == 0)
            return [CalculationCheckOutcome.NotEvaluated(LenderTotalsIntegrityCheckKey,
                "No ledgered charge-register entries for this snapshot — the charge sheet is likely absent from this upload.")];

        var relatedIds = chargeEntries.Select(e => e.CalculationLedgerEntryId).ToList();
        var openCharges = context.Model.Charges.Open;

        var perLenderTotal = openCharges
            .GroupBy(c => c.LatestChargeHolderNormalized)
            .Sum(g => g.Sum(c => c.CurrentAmount ?? 0m));
        var modelGrandTotal = context.Model.Charges.TotalOpenAmount;

        if (perLenderTotal != modelGrandTotal)
            return [CalculationCheckOutcome.Triggered(LenderTotalsIntegrityCheckKey, CalculationDiscrepancySeverity.Critical,
                System.Text.Json.JsonSerializer.Serialize(new { perLenderTotal, modelGrandTotal }), relatedIds)];

        return [CalculationCheckOutcome.NotTriggered(LenderTotalsIntegrityCheckKey, relatedIds)];
    }
}
