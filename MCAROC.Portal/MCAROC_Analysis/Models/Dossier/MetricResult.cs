using System.Globalization;

namespace MCAROC_Analysis.Models.Dossier;

/// <summary>One computed metric, always carrying its provenance. A metric with an
/// <see cref="InsufficiencyReason"/> renders as an explicit "not enough data" line — never a bare 0,
/// never a blank. The contract for the whole derived layer is <c>docs/analytics-catalogue.json</c>.
///
/// Design rules (do not relax):
/// <list type="bullet">
///   <item>Pure function output — computed from already-loaded entities, no I/O.</item>
///   <item>Not a score — a <see cref="MetricResult"/> is a single fact; never combine several into an index.</item>
///   <item>Every metric names its <see cref="Inputs"/> as <c>Entity.Field</c> strings and its <see cref="Period"/>.</item>
/// </list></summary>
public sealed record MetricResult(
    string Label,
    decimal? Value,
    MetricUnit Unit,
    string Period,
    IReadOnlyList<string> Inputs,
    string? InsufficiencyReason)
{
    /// <summary>True when the metric produced a real value (not an "insufficient data" placeholder).</summary>
    public bool HasValue => Value is not null && InsufficiencyReason is null;

    public static MetricResult Ok(string label, decimal value, MetricUnit unit, string period, params string[] inputs) =>
        new(label, value, unit, period, inputs, null);

    /// <summary>The metric could not be computed for a documented reason (one FY of data, a zero
    /// denominator, an inferred cash-flow year, …). Renders the reason, not a number.</summary>
    public static MetricResult Insufficient(string label, MetricUnit unit, string reason, params string[] inputs) =>
        new(label, null, unit, "—", inputs, reason);

    /// <summary>Display string for the value alone (no label): "12.4%", "1.8x", "₹1,240 Cr", "412 days",
    /// "37", "6.2 yrs". When there is no value, the insufficiency reason.</summary>
    public string DisplayValue()
    {
        if (InsufficiencyReason is not null) return InsufficiencyReason;
        if (Value is not { } v) return "—";
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
/// <c>IReadOnlyList&lt;MetricGroup&gt;</c> through the same components.</summary>
public sealed record MetricGroup(string Title, IReadOnlyList<MetricResult> Metrics)
{
    public bool HasAny => Metrics.Count > 0;
}
