namespace MCAROC_Analysis.Services.CalculationAssurance.Checks;

/// <summary>The concrete, testable proof that "NotEvaluated is not a passing result" holds end-to-end
/// from metric to ledger to check: for a short list of must-have calc keys, a ledgered
/// InsufficiencyReason always surfaces as this check's own NotEvaluated — never a manufactured
/// Triggered/NotTriggered verdict over absent data. Never itself a business-severity finding; it exists
/// purely to guard the chain.</summary>
public static class DataSufficiencyGuardCheck
{
    private static readonly string[] MustHaveCalculationKeyPrefixes =
    [
        "CapitalReconciliation.",
        "FinancialTrend.NetWorthGrowthYoy",
        "FinancialTrend.RevenueCagr"
    ];

    public static IReadOnlyList<CalculationCheckOutcome> Run(CalculationCheckContext context)
    {
        var outcomes = new List<CalculationCheckOutcome>();

        foreach (var prefix in MustHaveCalculationKeyPrefixes)
        {
            var entry = context.LedgerEntries.FirstOrDefault(e => e.CalculationKey.StartsWith(prefix, StringComparison.Ordinal));
            if (entry is null) continue; // not ledgered at all this run — a different concern, not this guard's job

            var checkKey = $"DataSufficiencyGuard.{entry.CalculationKey}";
            outcomes.Add(entry.InsufficiencyReason is not null
                ? CalculationCheckOutcome.NotEvaluated(checkKey, entry.InsufficiencyReason, [entry.CalculationLedgerEntryId])
                : CalculationCheckOutcome.NotTriggered(checkKey, [entry.CalculationLedgerEntryId]));
        }

        return outcomes;
    }
}
