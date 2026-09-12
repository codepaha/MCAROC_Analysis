namespace MCAROC_Analysis.Models.Viz;

/// <summary>One point in a <see cref="ChartSeries"/>. <see cref="Value"/> is deliberately nullable —
/// a missing data point is a real, distinct state from a zero, and must render as a gap, never as 0.</summary>
public readonly record struct ChartTimePoint(ChartPeriod Period, decimal? Value);
