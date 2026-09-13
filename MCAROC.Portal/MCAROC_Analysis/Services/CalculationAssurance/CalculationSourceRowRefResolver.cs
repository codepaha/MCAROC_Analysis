using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.CalculationAssurance;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Resolves a MetricResult's "Entity.Field" input strings to real source rows, for the entity
/// types PR1's three covered MetricGroups actually use. A metric names a *field*, not a specific row —
/// for a multi-year figure (a CAGR, a YoY comparison) this cites the real row(s) that plausibly back it
/// (latest and, where relevant, the prior year) rather than parsing the metric's free-text Period label
/// to guess exactly which years contributed. That can under-cite a 3-year CAGR's oldest year, but a real,
/// partial citation is never a fabricated one — see CalculationLedgerEntry.HasUnresolvedProvenance for
/// the case where nothing at all could be resolved.</summary>
public static class CalculationSourceRowRefResolver
{
    public static IReadOnlyList<CalculationSourceRowRef> Resolve(
        IReadOnlyList<string> inputs, DossierModel model, CompanyProfile? companyProfile)
    {
        var refs = new List<CalculationSourceRowRef>();

        foreach (var input in inputs)
        {
            var entityType = input.Split('.', 2)[0];
            switch (entityType)
            {
                case "CompanyProfile":
                    if (companyProfile is not null)
                        refs.Add(From(companyProfile));
                    break;

                case "FinancialYearData":
                    var years = model.Financials.Standalone.OrderBy(f => f.FinancialYear).ToList();
                    if (years.Count > 0) refs.Add(From(years[^1]));
                    if (years.Count > 1) refs.Add(From(years[^2]));
                    break;

                case "RocCharge":
                    foreach (var charge in model.Charges.All)
                        refs.Add(From(charge));
                    break;

                case "RocChargeEvent":
                    foreach (var charge in model.Charges.All)
                        foreach (var chargeEvent in charge.Events)
                            refs.Add(From(chargeEvent));
                    break;
            }
        }

        return refs.DistinctBy(r => (r.EntityType, r.EntityId)).ToList();
    }

    private static CalculationSourceRowRef From(CompanyProfile p) =>
        new("CompanyProfile", p.CompanyProfileId, p.SourceDocumentId, p.SourceSheetName, p.SourceRowNumber);

    private static CalculationSourceRowRef From(FinancialYearData f) =>
        new("FinancialYearData", f.FinancialId, f.SourceDocumentId, f.SourceSheetName, f.SourceRowNumber);

    private static CalculationSourceRowRef From(RocCharge c) =>
        new("RocCharge", c.ChargeId, c.SourceDocumentId, c.SourceSheetName, c.SourceRowNumber);

    private static CalculationSourceRowRef From(RocChargeEvent e) =>
        new("RocChargeEvent", e.ChargeEventId, e.SourceDocumentId, e.SourceSheetName, e.SourceRowNumber);
}
