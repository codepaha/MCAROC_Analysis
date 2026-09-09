using System.Text.Json;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Merges FindingDrafts sharing a (Section, ConsolidationGroup) key into one card — e.g. the two
/// revenue-decline codes collapse into one "Sustained Revenue Contraction" card instead of two separate
/// ones. Only merges genuinely-the-same-dimension signals; combining across dimensions (e.g. revenue +
/// profitability into "Financial Stress") is the deterministic cross-section engine's job, not this one's.
/// Even a single-member group is renamed to the group's stable code, so cross-section rules and
/// ReviewPriorityRules can check for the group code uniformly regardless of how many raw signals fired.</summary>
public static class FindingConsolidator
{
    public static List<FindingDraft> Consolidate(IReadOnlyList<FindingDraft> drafts)
    {
        var result = new List<FindingDraft>();
        var groups = drafts.Where(d => d.ConsolidationGroup is not null).GroupBy(d => (d.Section, d.ConsolidationGroup));

        foreach (var group in groups)
        {
            var members = group.ToList();
            var groupKey = group.Key.ConsolidationGroup!;
            var highest = members.OrderByDescending(m => m.Severity).First();

            if (members.Count == 1)
            {
                result.Add(highest with { Code = groupKey, ConsolidationGroup = null });
                continue;
            }

            result.Add(highest with
            {
                Code = groupKey,
                Title = GroupTitle(groupKey, highest.Title),
                SummaryText = string.Join(" ", members.Select(m => m.SummaryText)),
                MetricsJson = JsonSerializer.Serialize(members.ToDictionary(m => m.Code, m => (object?)ParseOrRaw(m.MetricsJson))),
                SupportingSignalCodes = members.Select(m => m.Code).ToList(),
                ConsolidationGroup = null
            });
        }

        result.AddRange(drafts.Where(d => d.ConsolidationGroup is null));
        return result;
    }

    private static string GroupTitle(string groupKey, string fallback) => groupKey switch
    {
        FinancialRules.RevenueTrendGroup => "Sustained Revenue Contraction",
        FinancialRules.ProfitabilityTrendGroup => "Persistent Operating/Profitability Weakness",
        _ => fallback
    };

    private static object? ParseOrRaw(string? json)
    {
        if (json is null) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { return json; }
    }
}
