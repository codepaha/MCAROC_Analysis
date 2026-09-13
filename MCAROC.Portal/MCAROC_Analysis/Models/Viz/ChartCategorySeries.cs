using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Models.Viz;

/// <summary>One category's bar/comparison values — the company's own value plus optional peer-median
/// and peer-max reference lines. <see cref="Median"/>/<see cref="Max"/> are independently nullable:
/// a category can have a company value with no peer reference data at all (not every metric has peers
/// reported), which must render as a bar with no reference marks, never a fabricated 0 reference.
/// <see cref="Accent"/> (#120, C9) lets a caller that knows the category's business meaning (e.g. "High
/// priority") pick a semantic bar color — always one of the closed <see cref="ChartAccent"/> values,
/// never a raw string a generic partial would have to trust.</summary>
public readonly record struct ChartCategoryPoint(string Category, decimal Value, decimal? Median, decimal? Max, ChartAccent Accent = ChartAccent.Brand);

/// <summary>The bar/comparison sibling of <see cref="ChartSeries"/> — same fail-closed <see cref="Create"/>
/// discipline, defined here as part of "the contract" for #116's five deferred partials
/// (<c>_MiniBars</c>, <c>_SplitBar</c>, <c>_RateBar</c>, <c>_PeerCompare</c>, <c>_ClosestPeers</c>) to
/// consume later, and #120's first real consumer (<c>_HorizontalBars.cshtml</c>).</summary>
public sealed class ChartCategorySeries
{
    public string Label { get; }
    public MetricUnit Unit { get; }
    public IReadOnlyList<string> Inputs { get; }
    public IReadOnlyList<ChartCategoryPoint> Points { get; }

    private ChartCategorySeries(string label, MetricUnit unit, IReadOnlyList<string> inputs, IReadOnlyList<ChartCategoryPoint> points)
    {
        Label = label;
        Unit = unit;
        Inputs = inputs;
        Points = points;
    }

    public static ChartCategorySeries Create(string label, MetricUnit unit, IReadOnlyList<string> inputs, IReadOnlyList<ChartCategoryPoint> points)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A chart category series must have a non-empty label.", nameof(label));
        if (unit is MetricUnit.Text or MetricUnit.Unspecified)
            throw new ArgumentException("A chart category series needs a specific, numeric MetricUnit — not Text or Unspecified.", nameof(unit));
        if (inputs is null || inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A chart category series must name at least one non-empty input (Entity.Field).", nameof(inputs));
        if (points is null || points.Count == 0)
            throw new ArgumentException("A chart category series must have at least one point.", nameof(points));
        if (points.Any(p => string.IsNullOrWhiteSpace(p.Category)))
            throw new ArgumentException("Every point must name a non-blank category.", nameof(points));

        var categories = points.Select(p => p.Category).ToList();
        if (categories.Count != categories.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new ArgumentException("A chart category series cannot have two points for the same category.", nameof(points));

        return new ChartCategorySeries(label, unit, inputs.ToArray(), points.ToArray());
    }
}
