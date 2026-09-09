namespace MCAROC_Analysis.Data.Entities;

/// <summary>One rule-engine + AI-synthesis pass for a request. Mirrors IngestionRun's versioning pattern:
/// re-running creates AnalysisRun N+1 rather than mutating a prior run, so analysis history is auditable.
/// Progress/severity counts are stored here (unlike IngestionRun's computed-on-read counts) because they're
/// written once at the end of a single-threaded orchestrator pass, not incremented by concurrent workers.</summary>
public class AnalysisRun
{
    public long AnalysisRunId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    /// <summary>Which IngestionRun's data this analysis was computed from — explicit lineage
    /// ("Analysis Run 3 was based on Ingestion Run 2"), not just implied by "the latest at the time".</summary>
    public long IngestionRunId { get; set; }

    /// <summary>Sequential per request: 1, 2, 3... incremented on each analysis attempt (including a
    /// crash-restart's fresh run — see AnalysisWorker's recovery sweep).</summary>
    public int RunNumber { get; set; }

    public AnalysisRunStatus Status { get; set; } = AnalysisRunStatus.Running;

    /// <summary>Bumped manually when rule logic changes materially.</summary>
    public string RuleEngineVersion { get; set; } = "1.0";
    public string PromptVersion { get; set; } = "1.0";

    public DateTime StartedDate { get; set; }
    public DateTime? CompletedDate { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Backend-computed by ReviewPriorityCalculator from Current/Trend findings only — never set
    /// or overridden by the AI synthesis call (see AiCrossSectionAnalysisService).</summary>
    public ReviewPriority? OverallReviewPriority { get; set; }

    public int CriticalFindingsCount { get; set; }
    public int ReviewFindingsCount { get; set; }
    public int WatchFindingsCount { get; set; }
    public int PositiveFindingsCount { get; set; }

    /// <summary>Structured 5-slot JSON: businessPerformance, financialPosition, borrowingSecurity,
    /// governanceCompliance, keyReviewItems[] — AI-authored, never a single free paragraph.</summary>
    public string? ExecutiveSummaryJson { get; set; }

    /// <summary>JSON array of {code, reason} for rules that reported NotEvaluated (missing/ambiguous
    /// inputs) — diagnostic only, never surfaced as an adverse finding.</summary>
    public string? DataSufficiencyNotesJson { get; set; }

    public List<AnalysisFinding> Findings { get; set; } = [];
}
