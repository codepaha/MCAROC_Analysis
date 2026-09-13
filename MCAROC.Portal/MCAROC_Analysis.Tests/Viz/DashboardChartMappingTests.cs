using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#116 (C6): the Dashboard-side mapping proves the shared contract serves the fleet-wide
/// domain with its *real* weekly/monthly dates, not a faked int. #120 (C9) adds the two mappings for
/// the Dashboard's other two charts (priority distribution, findings by section).</summary>
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

    // ── #120 (C9): ToPriorityDistributionSeries ──

    [Fact]
    public void ToPriorityDistributionSeries_orders_High_Medium_Low_regardless_of_dictionary_order()
    {
        var distribution = new Dictionary<ReviewPriority, int>
        {
            [ReviewPriority.Low] = 3,
            [ReviewPriority.High] = 7,
            [ReviewPriority.Medium] = 5,
        };

        var series = DashboardChartMapping.ToPriorityDistributionSeries(distribution)!;

        Assert.Equal(["High", "Medium", "Low"], series.Points.Select(p => p.Category));
        Assert.Equal([7m, 5m, 3m], series.Points.Select(p => p.Value));
    }

    [Fact]
    public void ToPriorityDistributionSeries_excludes_zero_count_priorities()
    {
        var distribution = new Dictionary<ReviewPriority, int> { [ReviewPriority.High] = 4, [ReviewPriority.Medium] = 0 };

        var series = DashboardChartMapping.ToPriorityDistributionSeries(distribution)!;

        Assert.Single(series.Points);
        Assert.Equal("High", series.Points[0].Category);
    }

    [Fact]
    public void ToPriorityDistributionSeries_returns_null_when_every_priority_is_zero_or_absent()
    {
        Assert.Null(DashboardChartMapping.ToPriorityDistributionSeries(new Dictionary<ReviewPriority, int>()));
        Assert.Null(DashboardChartMapping.ToPriorityDistributionSeries(new Dictionary<ReviewPriority, int> { [ReviewPriority.Low] = 0 }));
    }

    [Fact]
    public void ToPriorityDistributionSeries_assigns_the_correct_semantic_accent_per_priority()
    {
        var distribution = new Dictionary<ReviewPriority, int> { [ReviewPriority.High] = 1, [ReviewPriority.Medium] = 1, [ReviewPriority.Low] = 1 };
        var series = DashboardChartMapping.ToPriorityDistributionSeries(distribution)!;

        Assert.Equal(ChartAccent.Danger, series.Points.Single(p => p.Category == "High").Accent);
        Assert.Equal(ChartAccent.Warning, series.Points.Single(p => p.Category == "Medium").Accent);
        Assert.Equal(ChartAccent.Success, series.Points.Single(p => p.Category == "Low").Accent);
    }

    // ── #120 (C9): ToFindingsBySectionSeries ──

    [Fact]
    public void ToFindingsBySectionSeries_returns_null_when_there_are_no_findings()
    {
        Assert.Null(DashboardChartMapping.ToFindingsBySectionSeries([]));
    }

    [Fact]
    public void ToFindingsBySectionSeries_excludes_a_section_with_no_findings_at_all()
    {
        var findings = new List<SectionSeverityCount> { new(FindingSection.Charges, FindingSeverity.Critical, 2) };

        var series = DashboardChartMapping.ToFindingsBySectionSeries(findings)!;

        Assert.Single(series.Points);
        Assert.Equal("Charges", series.Points[0].Category);
    }

    [Fact]
    public void ToFindingsBySectionSeries_gives_a_section_an_explicit_zero_for_a_severity_it_has_none_of()
    {
        // Only Critical findings for Charges — Review/Watch must still appear as explicit 0 segments,
        // never omitted (this is what makes ChartStackedCategorySeries.Create's exact-set rule bite).
        var findings = new List<SectionSeverityCount> { new(FindingSection.Charges, FindingSeverity.Critical, 4) };

        var series = DashboardChartMapping.ToFindingsBySectionSeries(findings)!;
        var chargesPoint = series.Points.Single(p => p.Category == "Charges");

        Assert.Equal(3, chargesPoint.Segments.Count);
        Assert.Equal(4m, chargesPoint.Segments.Single(s => s.Label == "Critical").Value);
        Assert.Equal(0m, chargesPoint.Segments.Single(s => s.Label == "Review").Value);
        Assert.Equal(0m, chargesPoint.Segments.Single(s => s.Label == "Watch").Value);
    }

    [Fact]
    public void ToFindingsBySectionSeries_orders_sections_by_the_fixed_display_order_not_input_order()
    {
        var findings = new List<SectionSeverityCount>
        {
            new(FindingSection.Litigation, FindingSeverity.Watch, 1),
            new(FindingSection.CompanyProfile, FindingSeverity.Critical, 1),
        };

        var series = DashboardChartMapping.ToFindingsBySectionSeries(findings)!;

        Assert.Equal(["CompanyProfile", "Litigation"], series.Points.Select(p => p.Category));
    }

    [Fact]
    public void ToFindingsBySectionSeries_assigns_the_correct_semantic_accent_per_severity()
    {
        var findings = new List<SectionSeverityCount>
        {
            new(FindingSection.Charges, FindingSeverity.Critical, 1),
            new(FindingSection.Charges, FindingSeverity.Review, 1),
            new(FindingSection.Charges, FindingSeverity.Watch, 1),
        };

        var series = DashboardChartMapping.ToFindingsBySectionSeries(findings)!;
        var segments = series.Points[0].Segments;

        Assert.Equal(ChartAccent.Danger, segments.Single(s => s.Label == "Critical").Accent);
        Assert.Equal(ChartAccent.Warning, segments.Single(s => s.Label == "Review").Accent);
        Assert.Equal(ChartAccent.Muted, segments.Single(s => s.Label == "Watch").Accent);
    }
}
