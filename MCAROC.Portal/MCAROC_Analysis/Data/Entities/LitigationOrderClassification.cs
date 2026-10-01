namespace MCAROC_Analysis.Data.Entities;

/// <summary>What one court order actually grants or decides (epic #195's order-outcome taxonomy). Negation is
/// part of the taxonomy on purpose: a stay being vacated is <see cref="StayVacated"/>, never
/// <see cref="StayGranted"/>. Stored by name, so reordering members never changes persisted meaning.</summary>
public enum LitigationOrderOutcome
{
    FinePenalty,
    StayGranted,
    StayVacated,
    PossessionOrder,
    Injunction,
    Dismissal,
    DisposedSettled,
    InterimRelief,
    AdjournedNoSubstantiveOrder
}

/// <summary>One order document's validated outcome classification, produced inside a
/// <see cref="LitigationAiAnalysisRun"/> — the run's existing paid-call admission covers it, and an order whose
/// evidence and prompt are unchanged since an earlier run is carried forward instead of re-sent to the model.
/// Same fail-closed provenance shape as <see cref="LitigationCaseAiAnalysis"/>: <see cref="EvidenceJson"/> is the
/// exact bounded excerpt set sent to the model, and every outcome in <see cref="ClassificationJson"/> cites
/// page/chunk addresses from it (see <c>LitigationOrderClassifier</c>).
///
/// <see cref="RequestId"/>/<see cref="LitigationCaseId"/>/<see cref="LitigationCaseOrderId"/> are denormalized so
/// "every order with outcome X for this request" is one indexed query with no joins through the run.</summary>
public sealed class LitigationOrderClassification
{
    public long LitigationOrderClassificationId { get; set; }
    public long LitigationAiAnalysisRunId { get; set; }
    public long RequestId { get; set; }
    public long LitigationCaseId { get; set; }
    public long LitigationCaseOrderId { get; set; }
    public long LitigationOrderDocumentId { get; set; }

    public LitigationAiAnalysisItemStatus Status { get; set; } = LitigationAiAnalysisItemStatus.Pending;

    /// <summary>JSON array of <see cref="LitigationOrderOutcome"/> names — an order can decide several things in
    /// one hearing (same reasoning as <c>RocChargeEvent.FacilityTypesJson</c>). Empty unless
    /// <see cref="Status"/> is Completed.</summary>
    public string OutcomeTypesJson { get; set; } = "[]";

    /// <summary>The fine/penalty amount (₹) when the order states one; null when it doesn't, or when the order
    /// imposes no fine.</summary>
    public decimal? FineAmount { get; set; }
    public ClassificationConfidence? Confidence { get; set; }

    /// <summary>True when the order had more text than fits the bounded prompt — the classification then only
    /// reflects the leading excerpts, and anything shown to a user must say so.</summary>
    public bool EvidenceTruncated { get; set; }

    public string EvidenceJson { get; set; } = string.Empty;
    public string EvidenceHash { get; set; } = string.Empty;
    public string PromptHash { get; set; } = string.Empty;
    public string? RawResponseJson { get; set; }
    public string? ResponseHash { get; set; }
    /// <summary>The validated, normalized model output (outcomes with their evidence references).</summary>
    public string? ClassificationJson { get; set; }
    public string? FailureReason { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
