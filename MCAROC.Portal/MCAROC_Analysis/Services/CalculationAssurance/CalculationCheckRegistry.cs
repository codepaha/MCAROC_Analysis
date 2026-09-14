using MCAROC_Analysis.Services.CalculationAssurance.Checks;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>The deterministic check registry — physically separate from Services/Analysis/Rules/ (that
/// rule engine is off-limits to this lane per the working agreement). PR2 covers 3 concrete groups to
/// prove the shape works; the remaining categories from the issue follow the identical pattern in
/// follow-up work.</summary>
public static class CalculationCheckRegistry
{
    public static IReadOnlyList<CalculationCheckOutcome> RunAll(CalculationCheckContext context)
    {
        var outcomes = new List<CalculationCheckOutcome>();
        outcomes.AddRange(YoyTrendChecks.Run(context));
        outcomes.AddRange(CapitalReconciliationChecks.Run(context));
        outcomes.AddRange(ChargeLenderTotalsChecks.Run(context));
        outcomes.AddRange(ProvenanceCompletenessCheck.Run(context));
        outcomes.AddRange(DataSufficiencyGuardCheck.Run(context));
        return outcomes;
    }
}
