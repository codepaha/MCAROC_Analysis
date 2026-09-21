namespace MCAROC_Analysis.Data.Entities;

/// <summary>Durable lifecycle for one evidence-grounded LIT-05 analysis attempt. A run is request-scoped
/// and immutable once terminal; reruns create a new row so the prompt/evidence/result history remains
/// auditable.</summary>
public enum LitigationAiAnalysisRunStatus { Pending, InProgress, Completed, CompletedWithErrors, Failed }

/// <summary>Per-case and portfolio result lifecycle. Failed/unknown evidence is represented explicitly;
/// neither is silently converted into an adverse conclusion.</summary>
public enum LitigationAiAnalysisItemStatus { Pending, Completed, Failed, InsufficientEvidence }

public sealed class LitigationAiAnalysisRun
{
    public long LitigationAiAnalysisRunId { get; set; }
    public long RequestId { get; set; }
    public int RunNumber { get; set; }
    public LitigationAiAnalysisRunStatus Status { get; set; } = LitigationAiAnalysisRunStatus.Pending;
    public int AttemptCount { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureReason { get; set; }
    public List<LitigationCaseAiAnalysis> CaseAnalyses { get; set; } = [];
    public LitigationPortfolioAiAnalysis? PortfolioAnalysis { get; set; }
}

/// <summary>Persisted, evidence-addressable case output. EvidenceJson is the exact bounded source set sent
/// to the model; PromptHash and ResponseHash make prompt/result provenance inspectable without retaining a
/// hidden in-memory conversation.</summary>
public sealed class LitigationCaseAiAnalysis
{
    public long LitigationCaseAiAnalysisId { get; set; }
    public long LitigationAiAnalysisRunId { get; set; }
    public long LitigationCaseId { get; set; }
    public LitigationAiAnalysisItemStatus Status { get; set; } = LitigationAiAnalysisItemStatus.Pending;
    public string EvidenceJson { get; set; } = string.Empty;
    public string EvidenceHash { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string? RawResponseJson { get; set; }
    public string? ResponseHash { get; set; }
    public string? AnalysisJson { get; set; }
    public string? FailureReason { get; set; }
    public int? PromptTokenCount { get; set; }
    public int? ResponseTokenCount { get; set; }
    public DateTime? CompletedUtc { get; set; }
}

/// <summary>One bounded synthesis over the persisted per-case outputs of a run. It carries its own evidence
/// and prompt hashes so it cannot claim support from a case response that was never persisted.</summary>
public sealed class LitigationPortfolioAiAnalysis
{
    public long LitigationPortfolioAiAnalysisId { get; set; }
    public long LitigationAiAnalysisRunId { get; set; }
    public LitigationAiAnalysisItemStatus Status { get; set; } = LitigationAiAnalysisItemStatus.Pending;
    public string EvidenceJson { get; set; } = string.Empty;
    public string EvidenceHash { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string? RawResponseJson { get; set; }
    public string? ResponseHash { get; set; }
    public string? AnalysisJson { get; set; }
    public string? FailureReason { get; set; }
    public int? PromptTokenCount { get; set; }
    public int? ResponseTokenCount { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
