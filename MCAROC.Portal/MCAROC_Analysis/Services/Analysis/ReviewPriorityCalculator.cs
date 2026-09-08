using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Backend-computed OverallReviewPriority — never set or overridden by the AI synthesis call (its
/// JSON response has no priority field at all). Only Current/Trend findings count toward escalation; a
/// Historical Critical finding alone (e.g. an old resolved GST cancellation) must never drive priority to
/// High on its own.</summary>
public static class ReviewPriorityCalculator
{
    public static ReviewPriority Calculate(IReadOnlyList<FindingDraft> findings)
    {
        var active = findings.Where(f => f.TemporalStatus != TemporalStatus.Historical).ToList();
        var criticalFindings = active.Where(f => f.Severity == FindingSeverity.Critical).ToList();
        var reviewFindings = active.Where(f => f.Severity == FindingSeverity.Review).ToList();

        var hasDesignatedCritical = active.Any(f => ReviewPriorityRules.DesignatedCriticalCodes.Contains(f.Code));
        var hasCrossSectionCritical = active.Any(f => f.Section == FindingSection.CrossSection && f.Severity == FindingSeverity.Critical);
        var criticalDomainCount = criticalFindings.Select(f => f.Section).Distinct().Count();

        if ((criticalFindings.Count >= 2 && criticalDomainCount >= 2) || hasDesignatedCritical || hasCrossSectionCritical)
            return ReviewPriority.High;

        // Any Critical finding that didn't already escalate to High above (e.g. 2+ Critical findings
        // confined to a single domain) is still at least Medium — never falls through to Low.
        var hasMaterialTrend = active.Any(f => f.TemporalStatus == TemporalStatus.Trend && f.Severity >= FindingSeverity.Review);
        if (criticalFindings.Count >= 1 || reviewFindings.Count >= 2 || hasMaterialTrend)
            return ReviewPriority.Medium;

        return ReviewPriority.Low;
    }
}
