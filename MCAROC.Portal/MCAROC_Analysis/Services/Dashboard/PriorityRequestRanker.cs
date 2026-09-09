using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Ranks candidate requests for the Priority Requests table. Pure function, no DB. Candidates are
/// expected to already be pre-filtered by the caller to "High or Medium priority, or attention required" —
/// this ranker only orders and takes the top N, it does not itself decide eligibility.</summary>
public static class PriorityRequestRanker
{
    /// <summary>Explicit numeric rank rather than relying on ReviewPriority's declared enum order — same
    /// resilience reasoning as TopRiskIndicatorBuilder.SeverityRank. A null priority (no analysis run yet)
    /// ranks lowest, same as Low. Exposed publicly so RequestListQueryService's Priority sort uses this
    /// exact same rank rather than a second, potentially-drifting definition.</summary>
    public static readonly Dictionary<ReviewPriority, int> PriorityRank = new()
    {
        [ReviewPriority.High] = 2,
        [ReviewPriority.Medium] = 1,
        [ReviewPriority.Low] = 0
    };

    public static List<PriorityRequestRow> Rank(IReadOnlyList<PriorityRequestRow> candidates, int take)
    {
        return candidates
            .OrderByDescending(r => r.Priority is { } p ? PriorityRank[p] : -1)
            .ThenByDescending(r => r.CriticalFindingsCount)
            .ThenByDescending(r => r.Request.CreatedDate)
            .Take(take)
            .ToList();
    }
}
