namespace MCAROC_Analysis.Data.Entities;

/// <summary>The delivery gate for one report artifact — checked at dossier-download time
/// (CalculationArtifactGateService, built in PR4). Exactly two hold reasons exist; there is
/// deliberately no "AI hasn't finished yet" reason, since the async AI worker must never block
/// otherwise-safe delivery just because it hasn't completed (see CalculationAiAuditRunStatus). Rows are
/// never deleted — closing a hold only flips IsActive/Released* fields, preserving that a hold ever
/// existed.</summary>
public class CalculationArtifactHold
{
    public long CalculationArtifactHoldId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }
    public CalculationAuditSnapshot? Snapshot { get; set; }

    /// <summary>Null = all variants of this snapshot are held. Stored as the DossierVariant enum name —
    /// see CalculationDiscrepancy.Variant for why this isn't a typed reference.</summary>
    public string? Variant { get; set; }

    public CalculationArtifactHoldReason HoldReason { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Composite FK to CalculationDiscrepancy's alternate key — a hold can never cite a
    /// discrepancy from a different snapshot than the one it is holding.</summary>
    public long? SourceDiscrepancyId { get; set; }
    public CalculationDiscrepancy? SourceDiscrepancy { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? ReleasedUtc { get; set; }
    public string? ReleasedByReviewerName { get; set; }

    /// <summary>Internal-only justification (an exception reason, or "corrected in run N+1") — never
    /// surfaced on any customer-facing surface.</summary>
    public string? ReleaseNote { get; set; }
}
