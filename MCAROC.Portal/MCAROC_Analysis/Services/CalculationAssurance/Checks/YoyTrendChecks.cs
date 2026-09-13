using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance.Checks;

/// <summary>Independently re-derives the ledgered "Net worth growth (YoY)" figure (FinancialTrendMetrics)
/// straight from the two loaded FinancialYearData rows, via a genuinely separate code path from
/// DossierComputations.Metrics's own AddYoY/YoYPercent helpers — so a regression in that shared formula,
/// or a corrupted/stale ledger row, is caught rather than silently trusted. Almost always NotTriggered
/// under correct code; a real mismatch is exactly the kind of drift this check exists to catch.</summary>
public static class YoyTrendChecks
{
    public const string NetWorthGrowthYoyCheckKey = "FinancialTrend.NetWorthGrowthYoyIntegrity";

    private const decimal TolerancePercentagePoints = 0.1m;

    public static IReadOnlyList<CalculationCheckOutcome> Run(CalculationCheckContext context)
    {
        var ledgerEntry = context.FindLedgerEntry(CalculationKeySlug.For("FinancialTrend", "Net worth growth (YoY)"));
        if (ledgerEntry is null)
            return [CalculationCheckOutcome.NotEvaluated(NetWorthGrowthYoyCheckKey, "No ledgered \"Net worth growth (YoY)\" entry for this snapshot.")];

        var years = context.Model.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList();
        var latest = years.Count > 0 ? years[^1] : null;
        var prior = years.Count > 1 ? years[^2] : null;
        var relatedIds = new[] { ledgerEntry.CalculationLedgerEntryId };

        if (latest?.NetWorth is null || prior?.NetWorth is null || prior.NetWorth.Value == 0m)
            return [CalculationCheckOutcome.NotEvaluated(NetWorthGrowthYoyCheckKey,
                "Fewer than 2 reported years, or a missing/zero prior-year net worth.", relatedIds)];

        if (ledgerEntry.ValueNumeric is not { } stored)
            return [CalculationCheckOutcome.NotEvaluated(NetWorthGrowthYoyCheckKey,
                "Ledgered entry has no numeric value to cross-check.", relatedIds)];

        var recomputed = Math.Round((latest.NetWorth.Value - prior.NetWorth.Value) / Math.Abs(prior.NetWorth.Value) * 100m, 1);
        var delta = Math.Abs(recomputed - stored);
        var signDisagreement = recomputed != 0m && stored != 0m && Math.Sign(recomputed) != Math.Sign(stored);

        if (signDisagreement)
            return [CalculationCheckOutcome.Triggered(NetWorthGrowthYoyCheckKey, CalculationDiscrepancySeverity.Critical,
                DetailJson(recomputed, stored, delta), relatedIds)];

        if (delta > TolerancePercentagePoints)
            return [CalculationCheckOutcome.Triggered(NetWorthGrowthYoyCheckKey, CalculationDiscrepancySeverity.Material,
                DetailJson(recomputed, stored, delta), relatedIds)];

        return [CalculationCheckOutcome.NotTriggered(NetWorthGrowthYoyCheckKey, relatedIds)];
    }

    private static string DetailJson(decimal recomputed, decimal stored, decimal delta) =>
        System.Text.Json.JsonSerializer.Serialize(new { recomputed, stored, delta });
}
