using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Builds the canonical, invariant-culture payload CalculationLedgerService hashes into
/// InputHash — the actual resolved <em>values</em> behind a metric's inputs, not just the input names.
/// Two metrics computed from the same formula over different source data must never produce the same
/// InputHash. Resolves via CalculationInputResolver, the same pass CalculationSourceRowRefResolver uses
/// for citation — the two can never silently disagree on which inputs are traceable (the earlier bug: an
/// input this lane can't resolve must never be indistinguishable from one that resolved to an empty/null
/// value, and a metric mixing a resolved and an unresolved input must never look fully traceable just
/// because ONE of its inputs happened to resolve).</summary>
public static class CalculationInputCanonicalizer
{
    public static string BuildCanonicalInputPayload(
        IReadOnlyList<string> inputs, DossierModel model, CompanyProfile? companyProfile, IngestionRun? ingestionRun = null)
    {
        var facts = new List<string>();

        foreach (var input in inputs.OrderBy(i => i, StringComparer.Ordinal))
        {
            var resolved = CalculationInputResolver.ResolveOne(input, model, companyProfile, ingestionRun);
            if (resolved.Count == 0)
            {
                facts.Add($"{input}=unresolved");
                continue;
            }

            foreach (var r in resolved.OrderBy(r => r.EntityId))
                facts.Add($"{r.EntityType}[{r.EntityId}].{r.FieldOrLabel}={r.ValueText ?? "null"}");
        }

        return string.Join('|', facts);
    }
}
