using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.CalculationAssurance;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>The single resolution pass both CalculationSourceRowRefResolver (citation) and
/// CalculationInputCanonicalizer (hashing) build on — one metric input string resolves to zero or more
/// real source rows plus their actual values. Handles the two input shapes DossierComputations.Metrics
/// actually produces for the MetricGroups this lane covers:
/// <list type="bullet">
/// <item>"Entity.Field" — a typed property read via reflection off a real entity instance
/// (CompanyProfile, FinancialYearData, RocCharge, RocChargeEvent).</item>
/// <item>"Entity['Label']" — a label-matched row in a flat key/value collection
/// (FinancialParameter, FinancialFact), the same NormalizeFinancialLabel-style matching
/// DossierComputations.Metrics.LookupParameter/LookupFact use (duplicated here as a tiny, stable
/// normalization rather than exposing those private helpers across a large, actively-edited shared file).</item>
/// </list>
/// An input naming an entity type neither shape covers (a derived/computed concept like
/// "DossierComputations.SecurityTypeLabels", not a real source row) correctly resolves to nothing —
/// there is no row to cite, and the canonicalizer records that explicitly rather than fabricating one.</summary>
public static class CalculationInputResolver
{
    private static readonly Regex BracketInputPattern = new(@"^(?<type>[A-Za-z]+)\['(?<label>.*)'\]$", RegexOptions.Compiled);

    public static IReadOnlyList<CalculationResolvedInput> ResolveOne(string input, DossierModel model, CompanyProfile? companyProfile)
    {
        var bracketMatch = BracketInputPattern.Match(input);
        if (bracketMatch.Success)
            return ResolveBracketedInput(bracketMatch.Groups["type"].Value, bracketMatch.Groups["label"].Value, model);

        var parts = input.Split('.', 2);
        if (parts.Length != 2)
            return [];

        var (entityType, fieldName) = (parts[0], parts[1]);
        return entityType switch
        {
            "CompanyProfile" => companyProfile is null
                ? []
                : [FromReflection("CompanyProfile", companyProfile.CompanyProfileId, companyProfile, fieldName)],

            // Same latest-plus-prior approximation used throughout this lane — a multi-year aggregate's
            // older years aren't individually cited; see CalculationLedgerService's own note on this.
            "FinancialYearData" => ResolveLatestAndPrior(
                model.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList(), fieldName),

            "RocCharge" => model.Charges.All
                .OrderBy(c => c.ChargeId)
                .Select(c => FromReflection("RocCharge", c.ChargeId, c, fieldName))
                .ToList(),

            "RocChargeEvent" => model.Charges.All
                .OrderBy(c => c.ChargeId)
                .SelectMany(c => c.Events.OrderBy(e => e.ChargeEventId))
                .Select(e => FromReflection("RocChargeEvent", e.ChargeEventId, e, fieldName))
                .ToList(),

            _ => []
        };
    }

    private static IReadOnlyList<CalculationResolvedInput> ResolveLatestAndPrior(IReadOnlyList<FinancialYearData> orderedYears, string fieldName)
    {
        var result = new List<CalculationResolvedInput>();
        if (orderedYears.Count > 0) result.Add(FromReflection("FinancialYearData", orderedYears[^1].FinancialId, orderedYears[^1], fieldName));
        if (orderedYears.Count > 1) result.Add(FromReflection("FinancialYearData", orderedYears[^2].FinancialId, orderedYears[^2], fieldName));
        return result;
    }

    private static IReadOnlyList<CalculationResolvedInput> ResolveBracketedInput(string entityType, string label, DossierModel model)
    {
        var latestYear = model.Financials.LatestYear;
        if (latestYear is null)
            return [];

        var normalizedLabel = NormalizeLabel(label);
        return entityType switch
        {
            "FinancialParameter" => model.Financials.Parameters
                .Where(p => p.FinancialYear == latestYear && NormalizeLabel(p.ParameterName) == normalizedLabel)
                .Select(p => new CalculationResolvedInput(
                    "FinancialParameter", p.FinancialParameterId, p.ParameterName,
                    p.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? p.TextValue ?? p.RawValue,
                    p.SourceDocumentId, p.SourceSheetName, p.SourceRowNumber))
                .ToList(),

            "FinancialFact" => model.Financials.Facts
                .Where(f => f.Basis == FinancialBasis.Standalone && f.FinancialYear == latestYear && NormalizeLabel(f.Label) == normalizedLabel)
                .Select(f => new CalculationResolvedInput(
                    "FinancialFact", f.FinancialFactId, f.Label,
                    f.NumericValue?.ToString(CultureInfo.InvariantCulture) ?? f.RawValue,
                    f.SourceDocumentId, f.SourceSheetName, f.SourceRowNumber))
                .ToList(),

            _ => []
        };
    }

    private static string NormalizeLabel(string label) => Regex.Replace(label.Trim(), @"\s+", " ").ToLowerInvariant();

    private static CalculationResolvedInput FromReflection(string entityType, long entityId, ExtractedEntityBase entity, string fieldName)
    {
        var property = entity.GetType().GetProperty(fieldName, BindingFlags.Public | BindingFlags.Instance);
        var valueText = property?.GetValue(entity) switch
        {
            null => null,
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("O", CultureInfo.InvariantCulture),
            DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };
        return new CalculationResolvedInput(entityType, entityId, fieldName, valueText, entity.SourceDocumentId, entity.SourceSheetName, entity.SourceRowNumber);
    }
}
