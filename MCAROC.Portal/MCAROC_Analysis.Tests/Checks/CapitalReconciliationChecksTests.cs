using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance.Checks;
using static MCAROC_Analysis.Tests.Checks.CalculationCheckTestSupport;

namespace MCAROC_Analysis.Tests.Checks;

public class CapitalReconciliationChecksTests
{
    private const string LedgerKey = "CapitalReconciliation.McaMasterDataPaidUpCapitalVsStandaloneShareCapital";

    [Fact]
    public void No_ledgered_entry_is_NotEvaluated_for_both_checks()
    {
        var context = new CalculationCheckContext(CreateMinimalDossier(), []);

        var outcomes = CapitalReconciliationChecks.Run(context);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(CalculationCheckStatus.NotEvaluated, o.Status));
    }

    [Fact]
    public void Recompute_matching_the_stored_diff_and_below_threshold_is_NotTriggered_for_both()
    {
        // 80 - 78 = 2, below the 5 crore magnitude threshold.
        var model = CreateMinimalDossier([Year(1, 2025, shareCapital: 78m)], paidUpCapital: 80m);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 2m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcomes = CapitalReconciliationChecks.Run(context);

        Assert.All(outcomes, o => Assert.Equal(CalculationCheckStatus.NotTriggered, o.Status));
    }

    [Fact]
    public void A_recompute_mismatch_against_the_stored_value_is_Triggered_Critical()
    {
        var model = CreateMinimalDossier([Year(1, 2025, shareCapital: 78m)], paidUpCapital: 80m);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 999m) }; // stale/corrupted stored value
        var context = new CalculationCheckContext(model, ledger);

        var outcomes = CapitalReconciliationChecks.Run(context);

        var integrity = Assert.Single(outcomes, o => o.CheckKey == CapitalReconciliationChecks.RecomputeIntegrityCheckKey);
        Assert.Equal(CalculationCheckStatus.Triggered, integrity.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Critical, integrity.Severity);
    }

    [Fact]
    public void A_real_difference_past_the_magnitude_threshold_is_Triggered_Material()
    {
        // 80 - 60 = 20, past the 5 crore threshold, but the ledger's own stored value matches exactly —
        // RecomputeIntegrity must stay NotTriggered while MagnitudeThreshold fires.
        var model = CreateMinimalDossier([Year(1, 2025, shareCapital: 60m)], paidUpCapital: 80m);
        var ledger = new List<CalculationLedgerEntry> { LedgerEntry(1, LedgerKey, 20m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcomes = CapitalReconciliationChecks.Run(context);

        var integrity = Assert.Single(outcomes, o => o.CheckKey == CapitalReconciliationChecks.RecomputeIntegrityCheckKey);
        var magnitude = Assert.Single(outcomes, o => o.CheckKey == CapitalReconciliationChecks.MagnitudeThresholdCheckKey);
        Assert.Equal(CalculationCheckStatus.NotTriggered, integrity.Status);
        Assert.Equal(CalculationCheckStatus.Triggered, magnitude.Status);
        Assert.Equal(CalculationDiscrepancySeverity.Material, magnitude.Severity);
    }
}
