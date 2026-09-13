using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance.Checks;
using static MCAROC_Analysis.Tests.Checks.CalculationCheckTestSupport;

namespace MCAROC_Analysis.Tests.Checks;

public class ProvenanceCompletenessCheckTests
{
    [Fact]
    public void A_ledger_entry_with_unresolved_provenance_and_a_real_value_is_NotEvaluated()
    {
        var entry = LedgerEntry(1, "FinancialTrend.SomeMetric", 42m);
        entry.HasUnresolvedProvenance = true;
        var context = new CalculationCheckContext(CreateMinimalDossier(), [entry]);

        var outcome = Assert.Single(ProvenanceCompletenessCheck.Run(context));

        Assert.Equal(CalculationCheckStatus.NotEvaluated, outcome.Status);
        Assert.Equal("ProvenanceCompleteness.FinancialTrend.SomeMetric", outcome.CheckKey);
        Assert.Contains(1L, outcome.RelatedLedgerEntryIds);
    }

    [Fact]
    public void A_resolved_entry_produces_no_outcome_at_all()
    {
        var entry = LedgerEntry(1, "FinancialTrend.SomeMetric", 42m);
        entry.HasUnresolvedProvenance = false;
        var context = new CalculationCheckContext(CreateMinimalDossier(), [entry]);

        Assert.Empty(ProvenanceCompletenessCheck.Run(context));
    }

    [Fact]
    public void An_insufficient_entry_with_unresolved_provenance_produces_no_outcome_since_there_is_nothing_to_trace()
    {
        var entry = LedgerEntry(1, "FinancialTrend.SomeMetric", value: null, insufficiencyReason: "no data");
        entry.HasUnresolvedProvenance = true; // shouldn't happen in practice, but even if flagged, no value means nothing to guard
        var context = new CalculationCheckContext(CreateMinimalDossier(), [entry]);

        Assert.Empty(ProvenanceCompletenessCheck.Run(context));
    }
}
