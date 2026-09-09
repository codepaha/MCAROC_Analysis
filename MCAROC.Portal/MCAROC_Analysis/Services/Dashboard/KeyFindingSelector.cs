using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Picks the single most material finding to headline a request in the Priority Requests table.
/// Pure function, no DB. A Historical finding is never selected, regardless of severity — matching the same
/// current-risk discipline used everywhere else on the dashboard.</summary>
public static class KeyFindingSelector
{
    public static DashboardFindingRow? Select(IReadOnlyList<DashboardFindingRow> findings)
    {
        var current = findings.Where(f => f.TemporalStatus != TemporalStatus.Historical).ToList();

        return TryTier(current, FindingSeverity.Critical, FindingSection.CrossSection)
            ?? TryTier(current, FindingSeverity.Critical, section: null)
            ?? TryTier(current, FindingSeverity.Review, FindingSection.CrossSection)
            ?? TryTier(current, FindingSeverity.Review, section: null);
    }

    private static DashboardFindingRow? TryTier(IReadOnlyList<DashboardFindingRow> findings, FindingSeverity severity, FindingSection? section)
    {
        var candidates = findings.Where(f => f.Severity == severity && (section is null || f.Section == section)).ToList();
        if (candidates.Count == 0) return null;

        return candidates
            .OrderByDescending(f => f.DisplayPriority)
            .ThenByDescending(f => f.ObservationDate)
            .First();
    }
}
