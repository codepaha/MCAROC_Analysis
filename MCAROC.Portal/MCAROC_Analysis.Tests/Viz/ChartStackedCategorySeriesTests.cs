using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#120 (C9): <see cref="ChartStackedCategorySeries.Create"/> is the only way to build one —
/// fail-closed, mirroring <see cref="ChartCategorySeries"/>/<see cref="ChartSeries"/>'s own discipline.
/// A category missing a declared segment or carrying an extra one throws; a negative segment value
/// throws (stacked width has no meaningful interpretation for one, unlike a plain category value).</summary>
public class ChartStackedCategorySeriesTests
{
    private static readonly string[] Segments = ["Critical", "Review", "Watch"];

    private static ChartStackedCategoryPoint Point(string category, decimal critical, decimal review, decimal watch) =>
        new(category,
        [
            new ChartStackedSegment("Critical", critical),
            new ChartStackedSegment("Review", review),
            new ChartStackedSegment("Watch", watch),
        ]);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_rejects_a_blank_label(string? label)
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create(label!, MetricUnit.Count, ["Entity.Field"], Segments, [Point("A", 1, 0, 0)]));
    }

    [Theory]
    [InlineData(MetricUnit.Text)]
    [InlineData(MetricUnit.Unspecified)]
    public void Create_rejects_non_numeric_units(MetricUnit unit)
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", unit, ["Entity.Field"], Segments, [Point("A", 1, 0, 0)]));
    }

    [Fact]
    public void Create_rejects_null_or_empty_inputs()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, null!, Segments, [Point("A", 1, 0, 0)]));
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, [], Segments, [Point("A", 1, 0, 0)]));
    }

    [Fact]
    public void Create_rejects_blank_or_duplicate_segment_labels()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], [], [Point("A", 1, 0, 0)]));
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], ["Critical", "critical"], [Point("A", 1, 0, 0)]));
    }

    [Fact]
    public void Create_rejects_null_or_empty_points()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, null!));
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, []));
    }

    [Fact]
    public void Create_rejects_duplicate_categories()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments,
                [Point("A", 1, 0, 0), Point("A", 2, 0, 0)]));
    }

    [Fact]
    public void A_point_missing_a_declared_segment_throws()
    {
        var incomplete = new ChartStackedCategoryPoint("A", [new ChartStackedSegment("Critical", 1), new ChartStackedSegment("Review", 0)]);
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, [incomplete]));
    }

    [Fact]
    public void A_point_with_an_extra_undeclared_segment_throws()
    {
        var extra = new ChartStackedCategoryPoint("A",
        [
            new ChartStackedSegment("Critical", 1), new ChartStackedSegment("Review", 0),
            new ChartStackedSegment("Watch", 0), new ChartStackedSegment("Positive", 5),
        ]);
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, [extra]));
    }

    [Fact]
    public void A_negative_segment_value_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, [Point("A", -1, 0, 0)]));
    }

    [Fact]
    public void TotalFor_sums_correctly_including_an_all_zero_point()
    {
        var series = ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments,
            [Point("A", 3, 2, 1), Point("B", 0, 0, 0)]);

        Assert.Equal(6m, series.TotalFor(series.Points[0]));
        Assert.Equal(0m, series.TotalFor(series.Points[1]));
    }

    [Fact]
    public void A_point_may_declare_its_segments_in_any_order_as_long_as_the_set_matches()
    {
        var outOfOrder = new ChartStackedCategoryPoint("A",
        [
            new ChartStackedSegment("Watch", 1), new ChartStackedSegment("Critical", 2), new ChartStackedSegment("Review", 3),
        ]);

        var series = ChartStackedCategorySeries.Create("Findings", MetricUnit.Count, ["Entity.Field"], Segments, [outOfOrder]);
        Assert.Equal(6m, series.TotalFor(series.Points[0]));
    }
}
