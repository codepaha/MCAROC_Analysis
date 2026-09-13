using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Derives a CalculationLedgerEntry's stable-ish machine key from a MetricResult's human label —
/// shared by CalculationLedgerService (which writes ledger rows under this key) and the deterministic
/// checks (PR2, which look a ledger row up by this same key), so the two sides can never silently drift
/// apart into two different slugging rules.</summary>
public static class CalculationKeySlug
{
    public static string For(string groupKeyPrefix, string metricLabel) => $"{groupKeyPrefix}.{Slugify(metricLabel)}";

    /// <summary>E.g. "Net debt / EBITDA" -&gt; "NetDebtEbitda". Good enough for v1: these labels change
    /// only when someone deliberately edits DossierComputations.Metrics.cs, at which point CalcVersion is
    /// the intended place to record an intentional formula change anyway.</summary>
    private static string Slugify(string label)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(label, "[A-Za-z0-9]+"))
            sb.Append(char.ToUpperInvariant(m.Value[0])).Append(m.Value[1..].ToLowerInvariant());
        return sb.Length > 0 ? sb.ToString() : "Metric";
    }
}
