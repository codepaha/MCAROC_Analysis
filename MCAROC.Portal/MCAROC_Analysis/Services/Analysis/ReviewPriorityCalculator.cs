using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Backend-computed OverallReviewPriority — never set or overridden by the AI synthesis call (its
/// JSON response has no priority field at all). Only Current/Trend findings count toward escalation; a
/// Historical Critical finding alone (e.g. an old resolved GST cancellation) must never drive priority to
/// High on its own.
///
/// Calculate (pre-persist, from FindingDraft) and Explain (post-persist, from AnalysisFinding) share one
/// internal Evaluate over the minimal FindingSignal projection, so there is exactly one copy of the
/// branching logic — a hand-written second copy could silently drift from the first.</summary>
public static class ReviewPriorityCalculator
{
    public static ReviewPriority Calculate(IReadOnlyList<FindingDraft> findings) =>
        Evaluate(findings.Select(FindingSignal.From).ToList()).Priority;

    /// <summary>Re-derives the priority and every reason it fired, from the same AnalysisFinding rows
    /// already persisted for a run. Excludes findings added by the AI cross-section synthesis pass (tagged
    /// with AnalysisOrchestrator.AiCrossSectionCodePrefix) — those are appended to the run AFTER
    /// OverallReviewPriority was already computed and stored, and per this type's own contract must never
    /// affect it; including them here would let Explain disagree with the stored value for the exact
    /// reason this class exists to prevent.</summary>
    public static ReviewPriorityExplanation Explain(IReadOnlyList<AnalysisFinding> findings)
    {
        var ruleAuthored = findings.Where(f => !f.Code.StartsWith(AnalysisOrchestrator.AiCrossSectionCodePrefix, StringComparison.Ordinal));
        return Evaluate(ruleAuthored.Select(FindingSignal.From).ToList());
    }

    private static ReviewPriorityExplanation Evaluate(IReadOnlyList<FindingSignal> signals)
    {
        var active = signals.Where(f => f.TemporalStatus != TemporalStatus.Historical).ToList();
        var criticalFindings = active.Where(f => f.Severity == FindingSeverity.Critical).ToList();
        var reviewFindings = active.Where(f => f.Severity == FindingSeverity.Review).ToList();

        var hasMultiDomainCritical = criticalFindings.Count >= 2 && criticalFindings.Select(f => f.Section).Distinct().Count() >= 2;
        var hasDesignatedCritical = active.Any(f => ReviewPriorityRules.DesignatedCriticalCodes.Contains(f.Code));
        var hasCrossSectionCritical = active.Any(f => f.Section == FindingSection.CrossSection && f.Severity == FindingSeverity.Critical);

        var highReasons = new List<ReviewPriorityReason>();
        if (hasMultiDomainCritical) highReasons.Add(ReviewPriorityReason.MultiDomainCritical);
        if (hasDesignatedCritical) highReasons.Add(ReviewPriorityReason.DesignatedCriticalFinding);
        if (hasCrossSectionCritical) highReasons.Add(ReviewPriorityReason.CrossSectionCritical);

        if (highReasons.Count > 0)
            return new ReviewPriorityExplanation(ReviewPriority.High, highReasons);

        // Any Critical finding that didn't already escalate to High above (e.g. 2+ Critical findings
        // confined to a single domain) is still at least Medium — never falls through to Low.
        var hasMaterialTrend = active.Any(f => f.TemporalStatus == TemporalStatus.Trend && f.Severity >= FindingSeverity.Review);

        var mediumReasons = new List<ReviewPriorityReason>();
        if (criticalFindings.Count >= 1) mediumReasons.Add(ReviewPriorityReason.SingleDomainCritical);
        if (reviewFindings.Count >= 2) mediumReasons.Add(ReviewPriorityReason.MultipleReviewFindings);
        if (hasMaterialTrend) mediumReasons.Add(ReviewPriorityReason.MaterialTrend);

        if (mediumReasons.Count > 0)
            return new ReviewPriorityExplanation(ReviewPriority.Medium, mediumReasons);

        return new ReviewPriorityExplanation(ReviewPriority.Low, []);
    }
}
