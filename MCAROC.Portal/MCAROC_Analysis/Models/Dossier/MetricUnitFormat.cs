using System.Globalization;

namespace MCAROC_Analysis.Models.Dossier;

/// <summary>The one place a <see cref="MetricUnit"/> value turns into display text — extracted so
/// <see cref="MetricResult.DisplayValue"/> and the C6 chart contract's sparkline fallback table (which
/// carries the same <see cref="MetricUnit"/> at the series level) can never drift from each other by
/// each reimplementing "12.40%" / "₹1,240 Cr" / "1.80x" independently. Always invariant-culture — this
/// is machine-consistent display text, not locale-sensitive prose.</summary>
public static class MetricUnitFormat
{
    public static string Format(decimal value, MetricUnit unit)
    {
        var n = value.ToString(value == Math.Truncate(value) ? "N0" : "N2", CultureInfo.InvariantCulture);
        return unit switch
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
