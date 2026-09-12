using System.Globalization;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>Maps the fleet-wide Dashboard's own trend shape into the shared <see cref="ChartSeries"/>
/// contract — proves the contract serves both domains, not just the single-request dossier side. Does
/// NOT touch <c>Views/Dashboard/Index.cshtml</c> or replace its Chart.js canvas; that migration is
/// explicitly C9's (#120) scope. Unit-tested only in this PR.</summary>
public static class DashboardChartMapping
{
    public static ChartSeries ToChartSeries(IReadOnlyList<TrendPoint> trend, TrendGranularity granularity)
    {
        var points = trend
            .Select(t => new ChartTimePoint(
                ChartPeriod.ForDate(t.BucketStart, FormatLabel(t.BucketStart, granularity)),
                t.Created))
            .ToList();

        return ChartSeries.Create("Requests Created", MetricUnit.Count, ["RequestSummaryRow.CreatedDate"], points);
    }

    // InvariantCulture on purpose: a chart label is machine-generated display text, not locale-sensitive
    // prose, and letting it depend on the server's ambient culture (as DetailsFormat.Money() did before
    // #123/C5b's fix) would make it non-deterministic across dev machines and CI runners — e.g. "MMM"
    // renders "Sept" instead of "Sep" for September under some cultures' calendar data.
    private static string FormatLabel(DateOnly bucketStart, TrendGranularity granularity) => granularity switch
    {
        TrendGranularity.Monthly => bucketStart.ToString("MMM yyyy", CultureInfo.InvariantCulture),
        _ => bucketStart.ToString("dd MMM yyyy", CultureInfo.InvariantCulture),
    };
}
