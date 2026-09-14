using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Reads CalculationAssurance:Mode consistently across every calculation-assurance service —
/// shared so ledger persistence, deterministic checks, the AI worker, and the delivery gate can never
/// silently disagree on how the flag is parsed or what it defaults to.</summary>
public static class CalculationAssuranceConfig
{
    public static CalculationAssuranceMode ParseMode(IConfiguration config) =>
        Enum.TryParse<CalculationAssuranceMode>(config["CalculationAssurance:Mode"], ignoreCase: true, out var mode)
            ? mode
            : CalculationAssuranceMode.Off;
}
