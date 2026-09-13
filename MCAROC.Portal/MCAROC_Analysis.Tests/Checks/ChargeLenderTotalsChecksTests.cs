using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance.Checks;
using static MCAROC_Analysis.Tests.Checks.CalculationCheckTestSupport;

namespace MCAROC_Analysis.Tests.Checks;

/// <summary>The sum-of-parts invariant this check enforces is mathematically guaranteed to hold given
/// today's grouping logic (every charge always lands in exactly one lender group), so — as intended, see
/// the check's own doc comment — there is no realistic input that trips Triggered; these tests cover the
/// NotEvaluated/NotTriggered paths the check can actually reach, proving the guard runs cleanly rather
/// than throwing or mis-classifying real data.</summary>
public class ChargeLenderTotalsChecksTests
{
    [Fact]
    public void No_ledgered_charge_register_entries_is_NotEvaluated()
    {
        var context = new CalculationCheckContext(CreateMinimalDossier(), []);

        var outcome = Assert.Single(ChargeLenderTotalsChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.NotEvaluated, outcome.Status);
    }

    [Fact]
    public void Real_open_charges_across_multiple_lenders_reconcile_and_are_NotTriggered()
    {
        var charges = new List<Data.Entities.RocCharge>
        {
            OpenCharge(1, "STATE BANK OF INDIA", 610m),
            OpenCharge(2, "HDFC BANK LIMITED", 400m),
            OpenCharge(3, "STATE BANK OF INDIA", 50m)
        };
        var model = CreateMinimalDossier(openCharges: charges);
        var ledger = new List<Data.Entities.CalculationLedgerEntry> { LedgerEntry(1, "ChargeRegister.TotalOpenExposure", 1060m) };
        var context = new CalculationCheckContext(model, ledger);

        var outcome = Assert.Single(ChargeLenderTotalsChecks.Run(context));

        Assert.Equal(CalculationCheckStatus.NotTriggered, outcome.Status);
        Assert.Contains(1L, outcome.RelatedLedgerEntryIds);
    }
}
