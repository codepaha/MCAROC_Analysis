namespace MCAROC_Analysis.Models.Viz;

/// <summary>Pure, SVG-agnostic layout math for <c>Details/_Sparkline.cshtml</c> — kept separate from
/// the partial so the geometry itself is unit-testable without a Razor rendering harness. Every
/// x-position is the point's index in the already-sorted <see cref="ChartSeries.Points"/> list (uniform
/// spacing regardless of the real gap between two <see cref="ChartPeriod.ActualDate"/>s); the y-scale
/// is computed from the series' own actual value range, never assumed to start at 0.</summary>
public static class SparklineGeometry
{
    public readonly record struct PlottedPoint(double X, double Y, string Label, decimal Value);

    /// <summary>One contiguous run of non-null points. A run of exactly one point renders as a dot
    /// (a line needs two points); a run of two or more renders as a polyline. A null point between two
    /// runs is a visual gap — never interpolated across, never a zero substituted.</summary>
    public sealed record Segment(IReadOnlyList<PlottedPoint> Points);

    public sealed record Layout(
        IReadOnlyList<Segment> Segments,
        /// <summary>Y for a faint zero-reference line, only when the series' values cross sign
        /// (min &lt; 0 &lt; max). Null for an entirely positive or entirely negative series — it gets
        /// no baseline and uses the full available height.</summary>
        double? BaselineY);

    /// <summary>Returns null when every point is null — the partial's "Not enough data" text state,
    /// never an empty/degenerate <c>&lt;svg&gt;</c>.</summary>
    public static Layout? Compute(ChartSeries series, double width, double height, double padX, double padY)
    {
        var points = series.Points;
        var nonNullValues = points.Where(p => p.Value is not null).Select(p => p.Value!.Value).ToList();
        if (nonNullValues.Count == 0)
            return null;

        var min = nonNullValues.Min();
        var max = nonNullValues.Max();
        var innerWidth = width - 2 * padX;
        var innerHeight = height - 2 * padY;
        var xStep = points.Count > 1 ? innerWidth / (points.Count - 1) : 0;

        double YFor(decimal value)
        {
            if (max == min)
                return padY + innerHeight / 2; // a flat series — no variation to show, center the line
            var fraction = (double)((value - min) / (max - min));
            return padY + innerHeight * (1 - fraction); // SVG y grows downward
        }

        double? baselineY = min < 0 && max > 0 ? YFor(0m) : null;

        var segments = new List<Segment>();
        var current = new List<PlottedPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (point.Value is { } v)
            {
                current.Add(new PlottedPoint(padX + xStep * i, YFor(v), point.Period.Label, v));
            }
            else if (current.Count > 0)
            {
                segments.Add(new Segment(current));
                current = [];
            }
        }
        if (current.Count > 0)
            segments.Add(new Segment(current));

        return new Layout(segments, baselineY);
    }
}
