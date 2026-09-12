using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#116 (C6): <see cref="ChartSeries.Create"/> is the only way to build one — fail-closed,
/// mirroring <see cref="MetricResult"/>'s own constructor discipline. A caller cannot construct a
/// series with a blank label, no provenance, zero points, two points claiming the same period, or a
/// unit that isn't a real numeric quantity.</summary>
public class ChartSeriesTests
{
    private static ChartTimePoint Point(int year, decimal? value) =>
        new(ChartPeriod.ForFinancialYear(year), value);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_label(string? label)
    {
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create(label!, MetricUnit.Crore, ["Entity.Field"], [Point(2025, 1m)]));
    }

    [Theory]
    [InlineData(MetricUnit.Text)]
    [InlineData(MetricUnit.Unspecified)]
    public void Create_rejects_non_numeric_units(MetricUnit unit)
    {
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", unit, ["Entity.Field"], [Point(2025, 1m)]));
    }

    [Fact]
    public void Create_rejects_null_or_empty_inputs()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, null!, [Point(2025, 1m)]));
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, [], [Point(2025, 1m)]));
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, [""], [Point(2025, 1m)]));
    }

    [Fact]
    public void Create_rejects_null_or_empty_points()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"], null!));
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"], []));
    }

    [Fact]
    public void Create_rejects_two_points_sharing_the_same_period_sort_key()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"], [Point(2025, 1m), Point(2025, 2m)]));
    }

    [Fact]
    public void Create_sorts_points_strictly_ascending_by_sort_key_regardless_of_input_order()
    {
        var series = ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"],
            [Point(2027, 3m), Point(2025, 1m), Point(2026, 2m)]);

        Assert.Equal([2025, 2026, 2027], series.Points.Select(p => p.Period.SortKey));
    }

    [Fact]
    public void A_null_value_point_survives_construction_as_null_never_coerced_to_zero()
    {
        var series = ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"], [Point(2025, null)]);

        Assert.Null(series.Points[0].Value);
    }

    [Fact]
    public void FormatPoint_delegates_to_MetricUnitFormat_and_shows_a_dash_for_a_null_value()
    {
        var series = ChartSeries.Create("Revenue", MetricUnit.Crore, ["Entity.Field"],
            [Point(2025, 12.5m), Point(2026, null)]);

        Assert.Equal(MetricUnitFormat.Format(12.5m, MetricUnit.Crore), series.FormatPoint(series.Points[0]));
        Assert.Equal("—", series.FormatPoint(series.Points[1]));
    }
}
