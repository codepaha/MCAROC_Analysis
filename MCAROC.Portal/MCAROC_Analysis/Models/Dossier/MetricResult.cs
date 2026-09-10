using System.Globalization;

namespace MCAROC_Analysis.Models.Dossier;

/// <summary>One computed metric, always carrying its provenance. Constructed only through
/// <see cref="Ok"/> / <see cref="Insufficient"/> — the constructor is fail-closed: a metric MUST have
/// a non-empty label, a non-empty period, at least one non-empty input, and <em>exactly one</em> of a
/// value or an insufficiency reason. A metric with an <see cref="InsufficiencyReason"/> renders as an
/// explicit "not enough data" line — never a bare 0, never a blank dash.
///
/// Design rules (do not relax):
/// <list type="bullet">
///   <item>Pure function output — computed from already-loaded entities, no I/O.</item>
///   <item>Not a score — a <see cref="MetricResult"/> is a single fact; never combine several into an index.</item>
///   <item>Every metric names its <see cref="Inputs"/> as <c>Entity.Field</c> strings and its <see cref="Period"/>.</item>
/// </list>
/// Contract: <c>docs/analytics-catalogue.json</c>.</summary>
public sealed record MetricResult
{
    public string Label { get; private init; }
    public decimal? Value { get; private init; }
    public MetricUnit Unit { get; private init; }
    public string Period { get; private init; }
    public IReadOnlyList<string> Inputs { get; private init; }
    public string? InsufficiencyReason { get; private init; }

    private MetricResult(
        string label, decimal? value, MetricUnit unit, string period,
        IReadOnlyList<string> inputs, string? insufficiencyReason)
    {
        if (string.IsNullOrWhiteSpace(label))
            throw new ArgumentException("A metric must have a non-empty label.", nameof(label));
        if (string.IsNullOrWhiteSpace(period))
            throw new ArgumentException("A metric must state the period it covers.", nameof(period));
        if (inputs is null || inputs.Count == 0 || inputs.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A metric must name at least one non-empty input (Entity.Field).", nameof(inputs));

        var hasValue = value is not null;
        var hasReason = !string.IsNullOrWhiteSpace(insufficiencyReason);
        if (hasValue == hasReason)
            throw new ArgumentException(
                "A metric must carry exactly one of a value or an insufficiency reason — never both, never neither.");

        Label = label;
        Value = value;
        Unit = unit;
        Period = period;
        Inputs = inputs.ToArray();
        InsufficiencyReason = hasReason ? insufficiencyReason : null;
    }

    public static MetricResult Ok(string label, decimal value, MetricUnit unit, string period, params string[] inputs) =>
        new(label, value, unit, period, inputs, null);

    /// <summary>The metric could not be computed for a documented reason (one FY of data, a zero
    /// denominator, an inferred cash-flow year, …). Renders the reason, not a number.</summary>
    public static MetricResult Insufficient(string label, MetricUnit unit, string reason, params string[] inputs) =>
        new(label, null, unit, "n/a", inputs, reason);

    /// <summary>True when the metric produced a real value (not an "insufficient data" placeholder).</summary>
    public bool HasValue => Value is not null && InsufficiencyReason is null;

    /// <summary>Display string for the value alone (no label): "12.40%", "1.80x", "₹1,240 Cr",
    /// "412 days", "37", "6.20 yrs". When there is no value, the insufficiency reason (never a dash).</summary>
    public string DisplayValue()
    {
        if (InsufficiencyReason is not null) return InsufficiencyReason;
        if (Value is not { } v) return InsufficiencyReason ?? "n/a"; // unreachable — constructor guarantees one is set
        var n = v.ToString(v == Math.Truncate(v) ? "N0" : "N2", CultureInfo.InvariantCulture);
        return Unit switch
        {
            MetricUnit.Percent => $"{n}%",
            MetricUnit.Ratio => n,
            MetricUnit.Times => $"{n}x",
            MetricUnit.Crore => $"₹{n} Cr",
            MetricUnit.Rupees => $"₹{n}",
            MetricUnit.Days => $"{n} days",
            MetricUnit.Years => $"{n} yrs",
            MetricUnit.Count => n,
            _ => n
        };
    }
}

public enum MetricUnit { Count, Percent, Ratio, Times, Crore, Rupees, Days, Years }

/// <summary>A titled set of related metrics, rendered together (e.g. "Charge register",
/// "Financial trend &amp; leverage"). The dossier Snapshot and the portal tabs both render a
/// <c>IReadOnlyList&lt;MetricGroup&gt;</c> through the same components, sourced from the one
/// <c>DossierModel</c> the <c>DossierCache</c> hands to both.</summary>
public sealed record MetricGroup(string Title, IReadOnlyList<MetricResult> Metrics)
{
    public bool HasAny => Metrics.Count > 0;
}
