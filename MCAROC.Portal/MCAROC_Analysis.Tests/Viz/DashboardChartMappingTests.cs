using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#116 (C6): the Dashboard-side mapping proves the shared contract serves the fleet-wide
/// domain with its *real* weekly/monthly dates, not a faked int.</summary>
public class DashboardChartMappingTests
{
    [Fact]
    public void ToChartSeries_uses_count_unit_and_the_dashboards_own_provenance()
    {
        var trend = new List<TrendPoint> { new(new DateOnly(2026, 9, 1), 3) };

        var series = DashboardChartMapping.ToChartSeries(trend, TrendGranularity.Weekly);

        Assert.Equal("Requests Created", series.Label);
        Assert.Equal(MetricUnit.Count, series.Unit);
        Assert.Contains("RequestSummaryRow.CreatedDate", series.Inputs);
    }

    [Fact]
    public void Weekly_buckets_produce_genuinely_distinct_actual_dates_not_collapsed_to_a_shared_year()
    {
        var trend = new List<TrendPoint>
        {
            new(new DateOnly(2026, 9, 1), 2),
            new(new DateOnly(2026, 9, 8), 5),
            new(new DateOnly(2026, 9, 15), 1),
        };

        var series = DashboardChartMapping.ToChartSeries(trend, TrendGranularity.Weekly);

        var dates = series.Points.Select(p => p.Period.ActualDate).ToList();
        Assert.Equal(3, dates.Distinct().Count());
        Assert.All(dates, d => Assert.NotNull(d));
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 8), new DateOnly(2026, 9, 15)], dates);
    }

    [Fact]
    public void Weekly_label_shows_the_full_date_monthly_label_shows_month_and_year_only()
    {
        var date = new DateOnly(2026, 9, 12);
        var weekly = DashboardChartMapping.ToChartSeries([new TrendPoint(date, 1)], TrendGranularity.Weekly);
        var monthly = DashboardChartMapping.ToChartSeries([new TrendPoint(date, 1)], TrendGranularity.Monthly);

        Assert.Equal("12 Sep 2026", weekly.Points[0].Period.Label);
        Assert.Equal("Sep 2026", monthly.Points[0].Period.Label);
    }

    [Fact]
    public void A_zero_count_bucket_is_a_real_zero_not_a_missing_point()
    {
        var series = DashboardChartMapping.ToChartSeries([new TrendPoint(new DateOnly(2026, 9, 1), 0)], TrendGranularity.Weekly);
        Assert.Equal(0m, series.Points[0].Value);
    }
}
