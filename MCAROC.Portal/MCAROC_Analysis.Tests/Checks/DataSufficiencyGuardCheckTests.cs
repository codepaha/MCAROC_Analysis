using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance.Checks;
using static MCAROC_Analysis.Tests.Checks.CalculationCheckTestSupport;

namespace MCAROC_Analysis.Tests.Checks;

/// <summary>Proves "NotEvaluated is not a pass" holds end-to-end: a ledgered InsufficiencyReason always
/// surfaces as this guard's own NotEvaluated, never a manufactured Triggered/NotTriggered verdict.</summary>
public class DataSufficiencyGuardCheckTests
{
    [Fact]
    public void An_insufficient_must_have_metric_surfaces_as_NotEvaluated_not_a_false_pass()
    {
        var entry = LedgerEntry(1, "CapitalReconciliation.SomeReconciliation", value: null, insufficiencyReason: "no data on file");
        var context = new CalculationCheckContext(CreateMinimalDossier(), [entry]);

        var outcome = Assert.Single(DataSufficiencyGuardCheck.Run(context));

        Assert.Equal(CalculationCheckStatus.NotEvaluated, outcome.Status);
        Assert.Equal("no data on file", outcome.NotEvaluatedReason);
    }

    [Fact]
    public void A_populated_must_have_metric_is_NotTriggered()
    {
        var entry = LedgerEntry(1, "CapitalReconciliation.SomeReconciliation", value: 5m);
        var context = new CalculationCheckContext(CreateMinimalDossier(), [entry]);

        var outcome = Assert.Single(DataSufficiencyGuardCheck.Run(context));

        Assert.Equal(CalculationCheckStatus.NotTriggered, outcome.Status);
    }

    [Fact]
    public void A_must_have_key_not_ledgered_at_all_produces_no_outcome()
    {
        var context = new CalculationCheckContext(CreateMinimalDossier(), []);

        Assert.Empty(DataSufficiencyGuardCheck.Run(context));
    }
}
