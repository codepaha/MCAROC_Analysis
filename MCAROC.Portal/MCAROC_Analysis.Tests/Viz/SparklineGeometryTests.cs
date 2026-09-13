using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#116 (C6): the pure layout math _Sparkline.cshtml renders from — every edge case named in
/// the plan's review (all-null, one point, mixed-null gaps, zero as a real value, negative values with
/// a baseline, an entirely positive/negative series with none).</summary>
public class SparklineGeometryTests
{
    private const double W = 280, H = 56, PadX = 6, PadY = 6;

    private static ChartSeries Series(params decimal?[] values)
    {
        var points = values.Select((v, i) => new ChartTimePoint(ChartPeriod.ForFinancialYear(2020 + i), v)).ToList();
        return ChartSeries.Create("Revenue", MetricUnit.Crore, ["FinancialYearData.Revenue"], points);
    }

    [Fact]
    public void All_null_points_returns_null_layout_the_partials_not_enough_data_state()
    {
        var layout = SparklineGeometry.Compute(Series(null, null, null), W, H, PadX, PadY);
        Assert.Null(layout);
    }

    [Fact]
    public void Exactly_one_non_null_point_produces_a_single_segment_of_one_point_a_dot_not_a_line()
    {
        var layout = SparklineGeometry.Compute(Series(5m), W, H, PadX, PadY)!;

        Assert.Single(layout.Segments);
        Assert.Single(layout.Segments[0].Points);
    }

    [Fact]
    public void Mixed_nulls_break_the_series_into_separate_segments_never_interpolating_across_the_gap()
    {
        // [1, null, 2, 3] -> a lone dot for 1, then a 2-point line for [2, 3].
        var layout = SparklineGeometry.Compute(Series(1m, null, 2m, 3m), W, H, PadX, PadY)!;

        Assert.Equal(2, layout.Segments.Count);
        Assert.Single(layout.Segments[0].Points);
        Assert.Equal(2, layout.Segments[1].Points.Count);
    }

    [Fact]
    public void Every_periods_x_position_is_reserved_by_index_even_across_a_null_gap()
    {
        var layout = SparklineGeometry.Compute(Series(1m, null, 2m), W, H, PadX, PadY)!;
        var innerWidth = W - 2 * PadX;

        // Index 0 and index 2 (index 1 is the null gap) — spacing must reflect the real index gap,
        // not collapse to adjacent positions.
        var first = layout.Segments[0].Points[0];
        var second = layout.Segments[1].Points[0];
        Assert.Equal(PadX, first.X, 3);
        Assert.Equal(PadX + innerWidth, second.X, 3); // index 2 of 3 points (0,1,2) -> full width
    }

    [Fact]
    public void Zero_is_plotted_as_a_real_value_never_treated_as_a_gap()
    {
        var layout = SparklineGeometry.Compute(Series(1m, 0m, -1m), W, H, PadX, PadY)!;

        // All 3 points are non-null (0 included), so they form one single 3-point segment, not two
        // segments split around the "0" as if it were a gap.
        Assert.Single(layout.Segments);
        Assert.Equal(3, layout.Segments[0].Points.Count);
    }

    [Fact]
    public void Values_crossing_sign_get_a_zero_reference_baseline()
    {
        var layout = SparklineGeometry.Compute(Series(-2m, 3m), W, H, PadX, PadY)!;
        Assert.NotNull(layout.BaselineY);
    }

    [Fact]
    public void Entirely_positive_values_get_no_baseline()
    {
        var layout = SparklineGeometry.Compute(Series(1m, 2m, 3m), W, H, PadX, PadY)!;
        Assert.Null(layout.BaselineY);
    }

    [Fact]
    public void Entirely_negative_values_get_no_baseline()
    {
        var layout = SparklineGeometry.Compute(Series(-3m, -1m, -2m), W, H, PadX, PadY)!;
        Assert.Null(layout.BaselineY);
    }

    [Fact]
    public void A_flat_series_with_no_variation_does_not_divide_by_zero_and_centers_the_line()
    {
        var layout = SparklineGeometry.Compute(Series(5m, 5m, 5m), W, H, PadX, PadY)!;
        var expectedCenterY = PadY + (H - 2 * PadY) / 2;

        Assert.All(layout.Segments[0].Points, p => Assert.Equal(expectedCenterY, p.Y, 3));
    }

    [Fact]
    public void Every_plotted_point_carries_its_periods_label_and_real_value()
    {
        var layout = SparklineGeometry.Compute(Series(7.5m), W, H, PadX, PadY)!;
        var point = layout.Segments[0].Points[0];

        Assert.Equal("FY2020", point.Label);
        Assert.Equal(7.5m, point.Value);
    }
}
