using System.Globalization;
using System.Reflection;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Builds the canonical, invariant-culture payload CalculationLedgerService hashes into
/// InputHash — the actual resolved <em>values</em> behind a metric's "Entity.Field" input names, not just
/// the field names themselves. Two metrics computed from the same formula over different source data must
/// never produce the same InputHash (a fixed input-name list alone cannot tell them apart). Walks the same
/// entity types CalculationSourceRowRefResolver resolves, keeping both in sync by construction.</summary>
public static class CalculationInputCanonicalizer
{
    public static string BuildCanonicalInputPayload(IReadOnlyList<string> inputs, DossierModel model, CompanyProfile? companyProfile)
    {
        var facts = new List<string>();

        foreach (var input in inputs.OrderBy(i => i, StringComparer.Ordinal))
        {
            var parts = input.Split('.', 2);
            if (parts.Length != 2)
            {
                facts.Add($"{input}=unresolved");
                continue;
            }

            var (entityType, fieldName) = (parts[0], parts[1]);
            switch (entityType)
            {
                case "CompanyProfile":
                    if (companyProfile is not null)
                        facts.Add(Fact(entityType, companyProfile.CompanyProfileId, fieldName, GetValueText(companyProfile, fieldName)));
                    break;

                case "FinancialYearData":
                    // Same latest-plus-prior approximation as CalculationSourceRowRefResolver — see its
                    // own doc comment for why a multi-year aggregate's older years aren't individually cited.
                    var years = model.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList();
                    if (years.Count > 0) facts.Add(Fact(entityType, years[^1].FinancialId, fieldName, GetValueText(years[^1], fieldName)));
                    if (years.Count > 1) facts.Add(Fact(entityType, years[^2].FinancialId, fieldName, GetValueText(years[^2], fieldName)));
                    break;

                case "RocCharge":
                    foreach (var charge in model.Charges.All.OrderBy(c => c.ChargeId))
                        facts.Add(Fact(entityType, charge.ChargeId, fieldName, GetValueText(charge, fieldName)));
                    break;

                case "RocChargeEvent":
                    foreach (var charge in model.Charges.All.OrderBy(c => c.ChargeId))
                    foreach (var chargeEvent in charge.Events.OrderBy(e => e.ChargeEventId))
                        facts.Add(Fact(entityType, chargeEvent.ChargeEventId, fieldName, GetValueText(chargeEvent, fieldName)));
                    break;

                default:
                    // Not one of the 4 entity types this lane resolves (e.g. FinancialParameter['...']),
                    // or an entity that couldn't be matched — recorded as unresolved so the hash still
                    // reflects that this input contributed nothing traceable, rather than silently
                    // omitting it (which would make two genuinely-different-but-both-unresolved inputs
                    // hash identically by accident).
                    facts.Add($"{input}=unresolved");
                    break;
            }
        }

        return string.Join('|', facts);
    }

    private static string Fact(string entityType, long entityId, string fieldName, string? valueText) =>
        $"{entityType}[{entityId}].{fieldName}={valueText ?? "null"}";

    private static string? GetValueText(object entity, string fieldName)
    {
        var property = entity.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
        if (property is null) return null;

        return property.GetValue(entity) switch
        {
            null => null,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };
    }
}
