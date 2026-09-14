namespace MCAROC_Analysis.Data.Entities;

/// <summary>One AI second-line-review pass over a snapshot's ledger (the worker itself is built in
/// PR3). Unique per CalculationAuditSnapshotId — idempotency: exactly one AI audit run per snapshot.
/// RawResponseJson retains the actual model output (not just a hash — a hash alone proves nothing
/// without the content it was computed from), which is safe to keep in full because the bounded prompt
/// this worker sends never includes source-document text, OCR content, or PII beyond what is already
/// embedded in a numeric metric label.</summary>
public class CalculationAiAuditRun
{
    public long CalculationAiAuditRunId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }
    public CalculationAuditSnapshot? Snapshot { get; set; }

    public CalculationAiAuditRunStatus Status { get; set; } = CalculationAiAuditRunStatus.Pending;
    public int AttemptCount { get; set; }

    public string ModelId { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = "1.0";

    public int LedgerEntryCountSent { get; set; }
    public int RawCandidateCountReturned { get; set; }
    public int ValidatedCandidateCountAccepted { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Usage-metadata telemetry for rollout cost tracking — left null if the installed
    /// Google.GenAI SDK version doesn't expose it for this call shape.</summary>
    public int? PromptTokenCount { get; set; }
    public int? ResponseTokenCount { get; set; }

    /// <summary>The full raw model response text, retained indefinitely alongside the rest of this
    /// feature's audit trail so a disputed candidate can later be checked against exactly what the model
    /// said.</summary>
    public string? RawResponseJson { get; set; }

    /// <summary>SHA-256 of RawResponseJson — a cheap tamper-evidence/dedup check, never a substitute for
    /// the retained text above.</summary>
    public string? ResponseHash { get; set; }

    /// <summary>JSON array of full dropped-candidate objects (ledgerTag, claimType, expectedValue,
    /// actualValue, explanation, relatedLedgerTags, suggestedSeverity) plus a rejectReason each — so a
    /// disputed rejection is fully inspectable on its own without re-parsing RawResponseJson.</summary>
    public string? RejectedCandidatesJson { get; set; }
}
