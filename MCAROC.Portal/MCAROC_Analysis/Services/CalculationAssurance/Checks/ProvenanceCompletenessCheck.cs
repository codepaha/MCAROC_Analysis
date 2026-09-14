namespace MCAROC_Analysis.Services.CalculationAssurance.Checks;

/// <summary>Cross-cutting: any ledger entry with HasUnresolvedProvenance and a real value is an
/// automatic NotEvaluated — the mechanical guarantee that an audited-looking value nobody could actually
/// trace back to a source row never silently reads as "checked and fine."</summary>
public static class ProvenanceCompletenessCheck
{
    public const string CheckKeyPrefix = "ProvenanceCompleteness.";

    public static IReadOnlyList<CalculationCheckOutcome> Run(CalculationCheckContext context) =>
        context.LedgerEntries
            .Where(e => e.HasUnresolvedProvenance && e.ValueNumeric is not null)
            .Select(e => CalculationCheckOutcome.NotEvaluated(
                $"{CheckKeyPrefix}{e.CalculationKey}",
                "Source row provenance could not be resolved for this value.",
                [e.CalculationLedgerEntryId]))
            .ToList();
}
