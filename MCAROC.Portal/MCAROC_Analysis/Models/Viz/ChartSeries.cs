using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>A neutral, fail-closed chart-series contract (#116, C6) both the dossier's single-request
/// computations and the fleet-wide Dashboard can map into independently — <see cref="Create"/> is the
/// only way to build one, mirroring <see cref="MetricResult"/>'s own constructor discipline, so a
/// caller cannot construct a series with a blank label, no provenance, zero points, two points
/// claiming the same period, or a unit that isn't a real numeric quantity. A percentage series and a
/// crore series are visibly, structurally different from the moment they're constructed.</summary>
public sealed class ChartSeries
{
    public string Label { get; }
    public MetricUnit Unit { get; }
    public IReadOnlyList<string> Inputs { get; }
    /// <summary>Always stored strictly ascending by <see cref="ChartTimePoint.Period"/>.SortKey,
    /// regardless of the order points were passed to <see cref="Create"/> in.</summary>
    public IReadOnlyList<ChartTimePoint> Points { get; }

    private ChartSeries(string label, MetricUnit unit, IReadOnlyList<string> inputs, IReadOnlyList<ChartTimePoint> points)
    {
        Label = label;
        Unit = unit;
        Inputs = inputs;
        Points = points;
    }

    public static ChartSeries Create(string label, MetricUnit unit, IReadOnlyList<string> inputs, IReadOnlyList<ChartTimePoint> points)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A chart series must have a non-empty label.", nameof(label));
        if (unit is MetricUnit.Text or MetricUnit.Unspecified)
            throw new ArgumentException("A chart series needs a specific, numeric MetricUnit — not Text or Unspecified.", nameof(unit));
        if (inputs is null || inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A chart series must name at least one non-empty input (Entity.Field).", nameof(inputs));
        if (points is null || points.Count == 0)
            throw new ArgumentException("A chart series must have at least one point.", nameof(points));
        if (points.Any(p => string.IsNullOrWhiteSpace(p.Period.Label)))
            throw new ArgumentException("Every point's period must have a non-blank label.", nameof(points));
        if (points.Any(p => p.Period.ActualDate is { } d && d.DayNumber != p.Period.SortKey))
            throw new ArgumentException("A period's SortKey must match its ActualDate when one is present.", nameof(points));

        var sortKeys = points.Select(p => p.Period.SortKey).ToList();
        if (sortKeys.Count != sortKeys.Distinct().Count())
            throw new ArgumentException("A chart series cannot have two points with the same period.", nameof(points));

        var ordered = points.OrderBy(p => p.Period.SortKey).ToArray();
        return new ChartSeries(label, unit, inputs.ToArray(), ordered);
    }

    /// <summary>The exact same per-unit formatting <see cref="MetricResult.DisplayValue"/> uses — "—"
    /// for a missing point, never a fabricated 0. Used by _Sparkline.cshtml's fallback table so the
    /// text and the SVG can never show different numbers for the same point.</summary>
    public string FormatPoint(ChartTimePoint point) =>
        point.Value is { } v ? MetricUnitFormat.Format(v, Unit) : "—";
}
