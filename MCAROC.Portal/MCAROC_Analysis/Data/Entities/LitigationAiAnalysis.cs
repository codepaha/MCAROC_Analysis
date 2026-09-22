namespace MCAROC_Analysis.Data.Entities;

/// <summary>Durable lifecycle for one evidence-grounded LIT-05 analysis attempt. A run is request-scoped
/// and immutable once terminal; reruns create a new row so the prompt/evidence/result history remains
/// auditable.</summary>
public enum LitigationAiAnalysisRunStatus { Pending, InProgress, Completed, CompletedWithErrors, Failed }

/// <summary>Per-case and portfolio result lifecycle. Failed/unknown evidence is represented explicitly;
/// neither is silently converted into an adverse conclusion.</summary>
public enum LitigationAiAnalysisItemStatus { Pending, Completed, Failed, InsufficientEvidence }

/// <summary>What started this run — <see cref="Auto"/> is the pipeline coordinator (docs/pipeline-automation-
/// plan.md §4.2), <see cref="Reused"/> is a copy from another request's already-purchased snapshot (§4.2a,
/// never itself a new spend), <see cref="Manual"/> is the existing reviewer button, unchanged.</summary>
public enum LitigationAiAnalysisTrigger { Manual, Auto, Reused }

public sealed class LitigationAiAnalysisRun
{
    public long LitigationAiAnalysisRunId { get; set; }
    public long RequestId { get; set; }
    public int RunNumber { get; set; }
    public LitigationAiAnalysisRunStatus Status { get; set; } = LitigationAiAnalysisRunStatus.Pending;

    public LitigationAiAnalysisTrigger Trigger { get; set; } = LitigationAiAnalysisTrigger.Manual;
    /// <summary>The snapshot that triggered *this* run (this request's own newest fully-imported snapshot at
    /// admission time) — null only on rows predating this column.</summary>
    public long? TriggerSnapshotId { get; set; }
    /// <summary>The snapshot that was originally *purchased* — equal to <see cref="TriggerSnapshotId"/>
    /// unless this run's snapshot was itself reused from another request (§4.2a). The paid-call admission
    /// ledger's <c>analysis|{OriginSnapshotId}</c> scope, and the "at most one Auto run per snapshot" unique
    /// index, are both keyed on this, not on <see cref="TriggerSnapshotId"/> — that is what lets N requests
    /// sharing one purchased report still yield at most one paid auto analysis.</summary>
    public long? OriginSnapshotId { get; set; }

    public int AttemptCount { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
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
