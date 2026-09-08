using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Centralized, not embedded inline in ReviewPriorityCalculator — a designated critical-event
/// rule code alone (regardless of how many domains are affected) escalates OverallReviewPriority to High.
/// Add new codes here as they're introduced (e.g. a future COMPANY_UNDER_CIRP or AUDITOR_GOING_CONCERN
/// code), not inline in the calculator.</summary>
public static class ReviewPriorityRules
{
    public static readonly IReadOnlySet<string> DesignatedCriticalCodes = new HashSet<string>
    {
        FinancialRules.InterestCoverageCriticalCode,
        FinancialRules.NetWorthNegativeCode,
        AuditorRules.AdverseOpinionCode
    };
}
