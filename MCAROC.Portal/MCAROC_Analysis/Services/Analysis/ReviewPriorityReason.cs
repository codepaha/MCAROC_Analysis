namespace MCAROC_Analysis.Services.Analysis;

/// <summary>One condition ReviewPriorityCalculator's evaluator can find true — an enum, not free text, so
/// the UI can render a stable, exact label and a multi-condition explanation never collapses to just one
/// cause when several genuinely fired at once (e.g. a designated-critical code AND a material trend in
/// the same run).</summary>
public enum ReviewPriorityReason
{
    /// <summary>2+ Critical findings spanning 2+ distinct domains.</summary>
    MultiDomainCritical,

    /// <summary>A designated critical-event rule code fired (see ReviewPriorityRules.DesignatedCriticalCodes).</summary>
    DesignatedCriticalFinding,

    /// <summary>A Critical finding in the Cross-Section domain.</summary>
    CrossSectionCritical,

    /// <summary>At least one Critical finding, but not enough on its own to reach High.</summary>
    SingleDomainCritical,

    /// <summary>2+ Review-severity findings.</summary>
    MultipleReviewFindings,

    /// <summary>A Trend-status finding at Review severity or worse.</summary>
    MaterialTrend
}

public static class ReviewPriorityReasonExtensions
{
    /// <summary>Human-readable clause for the one-line UI explanation, e.g. "Medium — 2 Review findings".</summary>
    public static string Describe(this ReviewPriorityReason reason) => reason switch
    {
        ReviewPriorityReason.MultiDomainCritical => "Critical findings across multiple domains",
        ReviewPriorityReason.DesignatedCriticalFinding => "a designated critical finding",
        ReviewPriorityReason.CrossSectionCritical => "a cross-section Critical finding",
        ReviewPriorityReason.SingleDomainCritical => "a Critical finding",
        ReviewPriorityReason.MultipleReviewFindings => "multiple Review findings",
        ReviewPriorityReason.MaterialTrend => "a material trend",
        _ => reason.ToString()
    };
}
