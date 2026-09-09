using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Groups findings by Code and ranks by distinct companies affected — pure function, no DB.
/// Callers are expected to have already filtered rows to TemporalStatus != Historical and Severity !=
/// Positive; this builder does not re-filter, so a Historical or Positive row passed in will still be
/// counted (kept simple/composable — the exclusion is the caller's documented responsibility, matching
/// where DashboardQueryService applies it).</summary>
public static class TopRiskIndicatorBuilder
{
    /// <summary>Explicit numeric rank rather than relying on FindingSeverity's declared enum order — makes
    /// intent obvious and keeps this resilient if the enum is ever reordered.</summary>
    private static readonly Dictionary<FindingSeverity, int> SeverityRank = new()
    {
        [FindingSeverity.Critical] = 3,
        [FindingSeverity.Review] = 2,
        [FindingSeverity.Watch] = 1,
        [FindingSeverity.Positive] = 0
    };

    public static List<TopRiskIndicatorRow> Build(IReadOnlyList<DashboardFindingRow> rows, int take = 10)
    {
        return rows
            .GroupBy(r => r.Code)
            .Select(g =>
            {
                var top = g.OrderByDescending(r => SeverityRank[r.Severity]).First();
                return new TopRiskIndicatorRow(
                    Code: g.Key,
                    Title: top.Title,
                    Section: top.Section,
                    MaxSeverity: top.Severity,
                    FindingCount: g.Count(),
                    CompaniesAffected: g.Select(r => r.EntityKey).Distinct().Count());
            })
            .OrderByDescending(r => r.CompaniesAffected)
            .ThenByDescending(r => r.FindingCount)
            .Take(take)
            .ToList();
    }
}
