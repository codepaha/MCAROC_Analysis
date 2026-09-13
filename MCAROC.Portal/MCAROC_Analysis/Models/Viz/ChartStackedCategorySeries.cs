using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>One named segment of a stacked bar (e.g. "Critical" findings within one section). Segment
/// values are non-negative by construction — enforced in <see cref="ChartStackedCategorySeries.Create"/>
/// — because a stacked bar's width has no meaningful interpretation for a negative segment, unlike a
/// plain <see cref="ChartCategoryPoint.Value"/>, which might legitimately be a delta in some future
/// consumer.</summary>
public readonly record struct ChartStackedSegment(string Label, decimal Value, ChartAccent Accent = ChartAccent.Brand);

/// <summary>One category's full stack — must carry exactly the series' <see
/// cref="ChartStackedCategorySeries.SegmentLabels"/> set, no more, no fewer; a missing bucket must be an
/// explicit 0 segment, never an omitted one.</summary>
public readonly record struct ChartStackedCategoryPoint(string Category, IReadOnlyList<ChartStackedSegment> Segments);

/// <summary>The stacked-bar shape #116 (C6) never defined — one bar per category, split into named,
/// semantically-colored segments (e.g. findings-by-section-by-severity). Same fail-closed <see
/// cref="Create"/> discipline as <see cref="ChartSeries"/>/<see cref="ChartCategorySeries"/>.</summary>
public sealed class ChartStackedCategorySeries
{
    public string Label { get; }
    public MetricUnit Unit { get; }
    public IReadOnlyList<string> Inputs { get; }
    /// <summary>The canonical stack order (and complete segment-name set) every point must match
    /// exactly, e.g. <c>["Critical", "Review", "Watch"]</c>.</summary>
    public IReadOnlyList<string> SegmentLabels { get; }
    public IReadOnlyList<ChartStackedCategoryPoint> Points { get; }

    private ChartStackedCategorySeries(
        string label, MetricUnit unit, IReadOnlyList<string> inputs,
        IReadOnlyList<string> segmentLabels, IReadOnlyList<ChartStackedCategoryPoint> points)
    {
        Label = label;
        Unit = unit;
        Inputs = inputs;
        SegmentLabels = segmentLabels;
        Points = points;
    }

    public static ChartStackedCategorySeries Create(
        string label, MetricUnit unit, IReadOnlyList<string> inputs,
        IReadOnlyList<string> segmentLabels, IReadOnlyList<ChartStackedCategoryPoint> points)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A chart stacked-category series must have a non-empty label.", nameof(label));
        if (unit is MetricUnit.Text or MetricUnit.Unspecified)
            throw new ArgumentException("A chart stacked-category series needs a specific, numeric MetricUnit — not Text or Unspecified.", nameof(unit));
        if (inputs is null || inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A chart stacked-category series must name at least one non-empty input (Entity.Field).", nameof(inputs));
        if (segmentLabels is null || segmentLabels.Count == 0 || segmentLabels.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A chart stacked-category series must declare at least one non-blank segment label.", nameof(segmentLabels));
        if (segmentLabels.Distinct(StringComparer.OrdinalIgnoreCase).Count() != segmentLabels.Count)
            throw new ArgumentException("A chart stacked-category series cannot declare the same segment label twice.", nameof(segmentLabels));
        if (points is null || points.Count == 0)
            throw new ArgumentException("A chart stacked-category series must have at least one point.", nameof(points));
        if (points.Any(p => string.IsNullOrWhiteSpace(p.Category)))
            throw new ArgumentException("Every point must name a non-blank category.", nameof(points));

        var categories = points.Select(p => p.Category).ToList();
        if (categories.Distinct(StringComparer.OrdinalIgnoreCase).Count() != categories.Count)
            throw new ArgumentException("A chart stacked-category series cannot have two points for the same category.", nameof(points));

        var expectedSegments = new HashSet<string>(segmentLabels, StringComparer.OrdinalIgnoreCase);
        foreach (var point in points)
        {
            if (point.Segments is null || point.Segments.Count != segmentLabels.Count)
                throw new ArgumentException(
                    $"Point \"{point.Category}\" must carry exactly the series' {segmentLabels.Count} declared segment(s) — it has {point.Segments?.Count ?? 0}.",
                    nameof(points));

            var pointSegmentLabels = new HashSet<string>(point.Segments.Select(s => s.Label), StringComparer.OrdinalIgnoreCase);
            if (!pointSegmentLabels.SetEquals(expectedSegments))
                throw new ArgumentException(
                    $"Point \"{point.Category}\"'s segments must exactly match the series' declared segment labels — a missing bucket must be an explicit 0, never omitted.",
                    nameof(points));

            if (point.Segments.Any(s => s.Value < 0))
                throw new ArgumentException(
                    $"Point \"{point.Category}\" has a negative segment value — a stacked bar's segments are inherently non-negative.",
                    nameof(points));
        }

        return new ChartStackedCategorySeries(label, unit, inputs.ToArray(), segmentLabels.ToArray(), points.ToArray());
    }

    /// <summary>Sum of every segment's value for one point — the stacked bar's total width basis. A
    /// point whose every segment is 0 correctly returns 0; turning that into a zero-width (not
    /// divide-by-zero) bar is the rendering partial's responsibility, not this method's.</summary>
    public decimal TotalFor(ChartStackedCategoryPoint point) => point.Segments.Sum(s => s.Value);
}
