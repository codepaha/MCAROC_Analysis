using System.Globalization;
using System.Text;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>One labelled lookup for the offline name-resolution harness (issue #293, plan §5A.4).</summary>
/// <param name="InputName">The name as a requester would supply it.</param>
/// <param name="ExpectedIdentifier">The one correct CIN/LLPIN — or null when there is no single correct
/// answer (same-name duplicates with no distinguishing hint): the only right outcome then is to abstain, and
/// any auto-selection is a false accept.</param>
/// <param name="Category">Reporting bucket (e.g. "Labelled", "SuffixVariant", "Duplicate").</param>
public sealed record NameResolutionCase(string InputName, string? ExpectedIdentifier, string Category);

/// <summary>A candidate a strategy returned for a case, with its confidence score in [0, 1].</summary>
public sealed record NameCandidate(string Identifier, double Score);

public sealed record NameResolutionOutcome(NameResolutionCase Case, IReadOnlyList<NameCandidate> Candidates);

/// <summary>How a strategy performs if it auto-selects at <see cref="Threshold"/>.</summary>
/// <param name="AutoSelected">Cases where the top candidate reached the threshold and was not tied.</param>
/// <param name="Correct">Auto-selections that picked the expected identifier.</param>
/// <param name="Precision">Correct / AutoSelected — the number the §5A.4 gate (≥ 99.5%) is judged on.</param>
/// <param name="PrecisionLow">Lower bound of the 95% Wilson interval on precision; the gate should be read
/// against this, not the point estimate, when AutoSelected is small.</param>
/// <param name="Recall">Correct / cases that have a correct answer.</param>
public sealed record ThresholdMetrics(
    double Threshold, int Cases, int AutoSelected, int Correct,
    double Precision, double PrecisionLow, double PrecisionHigh, double Recall);

/// <param name="RetrievalRecall">Share of answerable cases whose expected identifier was among the candidates at
/// all — the ceiling any ranking on top of this retrieval can reach.</param>
public sealed record NameResolutionReport(
    string Strategy,
    int Cases,
    double RetrievalRecall,
    IReadOnlyList<ThresholdMetrics> Overall,
    IReadOnlyDictionary<string, IReadOnlyList<ThresholdMetrics>> ByCategory);

/// <summary>Pure scoring for the offline harness: given each case's candidates, what precision and recall
/// would auto-selection at each threshold achieve? Deliberately strategy-agnostic — I1 feeds it today's
/// prefix search and the new normalized lookup; I2's resolver plugs into the same report, which is how its
/// <c>T_high</c>/<c>M</c> are chosen from data rather than guessed.
///
/// Auto-select rule used here: the top candidate's score reaches the threshold <b>and</b> is strictly above
/// the runner-up. A tie is always an abstention — two equally good candidates is exactly the ambiguity the
/// resolver must hand to a human.</summary>
public static class NameResolutionEvaluator
{
    public static readonly IReadOnlyList<double> DefaultThresholds = [0.5, 0.6, 0.7, 0.8, 0.9, 1.0];

    public static NameResolutionReport Evaluate(
        string strategy, IReadOnlyList<NameResolutionOutcome> outcomes, IReadOnlyList<double>? thresholds = null)
    {
        thresholds ??= DefaultThresholds;
        var answerable = outcomes.Where(o => o.Case.ExpectedIdentifier is not null).ToList();
        var retrieved = answerable.Count(o => o.Candidates.Any(c => c.Identifier == o.Case.ExpectedIdentifier));

        var byCategory = outcomes
            .GroupBy(o => o.Case.Category)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ThresholdMetrics>)thresholds.Select(t => AtThreshold(g.ToList(), t)).ToList(),
                StringComparer.Ordinal);

        return new NameResolutionReport(
            strategy,
            outcomes.Count,
            answerable.Count == 0 ? 0 : (double)retrieved / answerable.Count,
            thresholds.Select(t => AtThreshold(outcomes, t)).ToList(),
            byCategory);
    }

    /// <summary>The identifier auto-selection would pick at <paramref name="threshold"/>, or null to abstain.</summary>
    public static string? AutoSelect(IReadOnlyList<NameCandidate> candidates, double threshold)
    {
        if (candidates.Count == 0) return null;
        var ranked = candidates.OrderByDescending(c => c.Score).ToList();
        var top = ranked[0];
        if (top.Score < threshold) return null;
        if (ranked.Count > 1 && ranked[1].Score >= top.Score) return null; // tie → ambiguous
        return top.Identifier;
    }

    /// <summary>95% Wilson score interval for a proportion — stays meaningful for small samples and at 0%/100%,
    /// unlike the normal approximation (which would report 100% ± 0 for 3 correct out of 3).</summary>
    public static (double Low, double High) WilsonInterval(int successes, int trials, double z = 1.959964)
    {
        if (trials == 0) return (0, 1);
        double n = trials, p = successes / n, z2 = z * z;
        var centre = (p + z2 / (2 * n)) / (1 + z2 / n);
        var half = z * Math.Sqrt(p * (1 - p) / n + z2 / (4 * n * n)) / (1 + z2 / n);
        return (Math.Max(0, centre - half), Math.Min(1, centre + half));
    }

    public static string FormatReport(NameResolutionReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## {report.Strategy}");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{report.Cases} cases; expected identifier retrieved at all for {report.RetrievalRecall:P1} of answerable cases.");
        sb.AppendLine();
        AppendTable(sb, "All cases", report.Overall);
        foreach (var (category, metrics) in report.ByCategory)
            AppendTable(sb, category, metrics);
        return sb.ToString();
    }

    private static ThresholdMetrics AtThreshold(IReadOnlyList<NameResolutionOutcome> outcomes, double threshold)
    {
        int selected = 0, correct = 0;
        foreach (var o in outcomes)
        {
            var pick = AutoSelect(o.Candidates, threshold);
            if (pick is null) continue;
            selected++;
            if (o.Case.ExpectedIdentifier is not null && pick == o.Case.ExpectedIdentifier) correct++;
        }

        var answerable = outcomes.Count(o => o.Case.ExpectedIdentifier is not null);
        var (low, high) = WilsonInterval(correct, selected);
        return new ThresholdMetrics(
            threshold, outcomes.Count, selected, correct,
            selected == 0 ? 0 : (double)correct / selected, low, high,
            answerable == 0 ? 0 : (double)correct / answerable);
    }

    private static void AppendTable(StringBuilder sb, string title, IReadOnlyList<ThresholdMetrics> metrics)
    {
        sb.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
        sb.AppendLine("| Threshold | Cases | Auto-selected | Correct | Precision | Precision 95% CI | Recall |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in metrics)
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {m.Threshold:0.00} | {m.Cases} | {m.AutoSelected} | {m.Correct} | {m.Precision:P2} | {m.PrecisionLow:P2} – {m.PrecisionHigh:P2} | {m.Recall:P2} |");
        sb.AppendLine();
    }
}
