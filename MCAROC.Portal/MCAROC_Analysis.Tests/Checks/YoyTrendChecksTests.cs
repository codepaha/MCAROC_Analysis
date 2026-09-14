using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance.Checks;
using static MCAROC_Analysis.Tests.Checks.CalculationCheckTestSupport;

namespace MCAROC_Analysis.Tests.Checks;

public class YoyTrendChecksTests
{
    private static readonly string CheckKey = YoyTrendChecks.NetWorthGrowthYoyCheckKey;
    private static readonly string LedgerKey = CalculationKeySlug.For("FinancialTrend", "Net worth growth (YoY)");

    [Fact]
    public void No_ledgered_entry_is_NotEvaluated()
    {
        var context = new CalculationCheckContext(CreateMinimalDossier(), []);

        var outcome = Assert.Single(YoyTrendChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.NotEvaluated, outcome.Status);
        Assert.Equal(CheckKey, outcome.CheckKey);
    }

    [Fact]
    public void Fewer_than_two_years_is_NotEvaluated()
    {
        var model = CreateMinimalDossier([Year(1, 2025, 100m)]);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 10m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcome = Assert.Single(YoyTrendChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.NotEvaluated, outcome.Status);
    }

    [Fact]
    public void Recompute_matching_the_ledgered_value_is_NotTriggered()
    {
        // (120 - 100) / |100| * 100 = 20.0
        var model = CreateMinimalDossier([Year(1, 2024, 100m), Year(2, 2025, 120m)]);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 20.0m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcome = Assert.Single(YoyTrendChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.NotTriggered, outcome.Status);
        Assert.Contains(1L, outcome.RelatedLedgerEntryIds);
    }

    [Fact]
    public void A_material_disagreement_within_the_same_sign_is_Triggered_Material()
    {
        // Recomputed is 20.0; ledgered claims 25.0 — same sign, outside the 0.1pp tolerance.
        var model = CreateMinimalDossier([Year(1, 2024, 100m), Year(2, 2025, 120m)]);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 25.0m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcome = Assert.Single(YoyTrendChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.Triggered, outcome.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Material, outcome.Severity);
    }

    [Fact]
    public void A_sign_disagreement_is_Triggered_Critical()
    {
        // Recomputed is +20.0 (growth); ledgered claims -20.0 (decline) — opposite sign.
        var model = CreateMinimalDossier([Year(1, 2024, 100m), Year(2, 2025, 120m)]);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, -20.0m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcome = Assert.Single(YoyTrendChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.Triggered, outcome.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Critical, outcome.Severity);
    }
}
