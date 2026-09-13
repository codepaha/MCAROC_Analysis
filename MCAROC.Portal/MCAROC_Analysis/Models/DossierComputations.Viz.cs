using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Models;

/// <summary>C6 (#116) chart-contract mappings — kept in their own file per this class's existing
/// <c>.Metrics.cs</c> file-splitting convention, separate from the Wave-4 metrics sections.</summary>
public static partial class DossierComputations
{
    /// <summary>The reference implementation for #116's shared contract: a real revenue-by-year
    /// series over standalone financial years, for <c>_FinancialsTab.cshtml</c>'s Summary section. A
    /// <c>null</c> revenue year stays <c>null</c> in <see cref="ChartTimePoint.Value"/> — never
    /// coerced to 0. <see cref="ChartSeries.Create"/>'s own sort makes pre-sorting the input
    /// redundant, but sorting first keeps this method's own reasoning simple.</summary>
    public static ChartSeries BuildRevenueTrendSeries(IReadOnlyList<FinancialYearData> years)
    {
        var points = years
            .OrderBy(y => y.FinancialYear)
            .Select(y => new ChartTimePoint(ChartPeriod.ForFinancialYear(y.FinancialYear), y.Revenue))
            .ToList();

        return ChartSeries.Create("Revenue", MetricUnit.Crore, ["FinancialYearData.Revenue"], points);
    }
}
