using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Keeps peer-comparison business semantics out of Razor. <see cref="Compare"/> is the purely
/// mathematical position of a company value vs the peer median; <see cref="DirectionFor"/> says whether
/// higher or lower is better for a given metric, so the view can colour a position without embedding the
/// rules itself.</summary>
public static class PeerComparisonDisplayRules
{
    /// <summary>A company value within ±this fraction of the peer median counts as "in line".</summary>
    public const decimal InLineTolerance = 0.05m;

    public static PeerPosition Compare(decimal? companyValue, decimal? peerMedian)
    {
        if (companyValue is not { } c || peerMedian is not { } m)
            return PeerPosition.NotComparable;

        var band = Math.Abs(m) * InLineTolerance;
        if (Math.Abs(c - m) <= band)
            return PeerPosition.InLine;

        return c > m ? PeerPosition.Above : PeerPosition.Below;
    }

    /// <summary>Whether a higher value is favourable for this metric. Matched case-insensitively on a
    /// keyword in the metric name (the sheet names carry units, e.g. "EBITDA Margin (%)").</summary>
    public static PeerMetricDirection DirectionFor(string metricName)
    {
        var n = (metricName ?? "").ToLowerInvariant();

        // Lower is better — leverage, working-capital days, cost/efficiency ratios.
        foreach (var k in LowerIsBetter)
            if (n.Contains(k)) return PeerMetricDirection.LowerIsBetter;

        // Higher is better — margins, returns, coverage, growth, liquidity.
        foreach (var k in HigherIsBetter)
            if (n.Contains(k)) return PeerMetricDirection.HigherIsBetter;

        return PeerMetricDirection.Unknown;
    }

    /// <summary>Is a given position favourable / adverse / neutral for this metric?</summary>
    public static string Sentiment(string metricName, PeerPosition position) => DirectionFor(metricName) switch
    {
        PeerMetricDirection.HigherIsBetter => position switch
        {
            PeerPosition.Above => "favourable",
            PeerPosition.Below => "adverse",
            PeerPosition.InLine => "neutral",
            _ => "unknown"
        },
        PeerMetricDirection.LowerIsBetter => position switch
        {
            PeerPosition.Above => "adverse",
            PeerPosition.Below => "favourable",
            PeerPosition.InLine => "neutral",
            _ => "unknown"
        },
        _ => "unknown"
    };

    private static readonly string[] LowerIsBetter =
    [
        "debt ratio", "debt / equity", "debt/equity", "inventory / sales", "debtors / sales",
        "payables / sales", "cash conversion cycle", "days"
    ];

    private static readonly string[] HigherIsBetter =
    [
        "margin", "return on", "roe", "roce", "coverage", "growth", "current ratio", "quick ratio",
        "revenue", "sales / net fixed assets"
    ];
}
