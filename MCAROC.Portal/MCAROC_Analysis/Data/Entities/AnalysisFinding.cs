namespace MCAROC_Analysis.Data.Entities;

/// <summary>One rule-engine (or deterministic cross-section, or AI-proposed cross-section) finding.
/// Rule-authored fields (Section/Severity/TemporalStatus/Code/Title/SummaryText/MetricsJson/
/// SupportingSignalsJson/DisplayPriority) are always set by the rule that produced it; WhyThisMatters/
/// RecommendedReview are filled in later, only for Critical/Review findings, by the validated AI synthesis
/// pass — never the reverse.</summary>
public class AnalysisFinding
{
    public long FindingId { get; set; }
    public long AnalysisRunId { get; set; }
    public long RequestId { get; set; }

    public FindingSection Section { get; set; }
    public FindingSeverity Severity { get; set; }

    /// <summary>Current / Historical / Trend — e.g. a past resolved GST cancellation is Historical, a live
    /// one is Current, "revenue declining 3 consecutive years" is Trend. ReviewPriorityCalculator only
    /// counts Current/Trend toward escalation — a Historical Critical finding alone must never drive
    /// OverallReviewPriority to High.</summary>
    public TemporalStatus TemporalStatus { get; set; }

    /// <summary>Stable rule identifier, e.g. "FIN_REVENUE_DECLINE_2Y" — treated like an API identifier
    /// once shipped (referenced by cross-section rules, ReviewPriorityRules.DesignatedCriticalCodes, AI
    /// narrative matching, and regression tests), not renamed casually.</summary>
    public string Code { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Rule-generated, always present regardless of whether AI synthesis ran/succeeded.</summary>
    public string SummaryText { get; set; } = string.Empty;

    /// <summary>Numbers directly underlying this finding, e.g. {"fy2024Revenue":48.62,"fy2025Revenue":36.46,"changePercent":-25.0}.</summary>
    public string? MetricsJson { get; set; }

    /// <summary>JSON array of related finding Codes (not free text) — e.g. ["FIN_EBITDA_NEGATIVE_TREND",
    /// "EPFO_WORKFORCE_DECLINE"]. The UI resolves codes to sibling finding cards, which is what makes
    /// cross-section linkage real navigation instead of cosmetic bullet text.</summary>
    public string? SupportingSignalsJson { get; set; }

    /// <summary>AI-filled, Critical/Review tier only, validated before being written (see
    /// AiCrossSectionAnalysisService) — null for Watch/Positive findings and whenever AI synthesis
    /// failed/was rejected for this specific code.</summary>
    public string? WhyThisMatters { get; set; }
    public string? RecommendedReview { get; set; }

    /// <summary>Concrete shape: {"entityType":"FinancialYearData","entityIds":[12,13],"sourceDocumentId":2,
    /// "sheet":"Standalone Financial Data","rows":[14,15]} — precise enough for a real evidence link later
    /// even though v1's "View Source" just jumps to the existing Details.cshtml section.</summary>
    public string? SourceReferenceJson { get; set; }

    public string? PeriodLabel { get; set; }
    public DateOnly? ObservationDate { get; set; }

    /// <summary>Rule-assigned weight used only to sort Watch/Positive findings before the UI caps them to
    /// the top 3-5 per section — keeps "which findings get shown" deterministic and rule-authored, not
    /// incidental database row order. Critical/Review always show in full regardless of this value.</summary>
    public int DisplayPriority { get; set; }
}
