namespace MCAROC_Analysis.Data.Entities;

/// <summary>The internal calculation-discrepancy workflow entity (built out in PR4 — the entity ships in
/// PR1's migration so the whole schema lands in a single migration). <see cref="PrimaryLedgerEntryId"/>
/// is required, never nullable, and is enforced via a composite foreign key on
/// (PrimaryLedgerEntryId, CalculationAuditSnapshotId) against CalculationLedgerEntry's own alternate
/// key — a discrepancy can never cite ledger evidence from a different snapshot than the one it belongs
/// to; that is a database constraint, not a service-layer convention. Non-primary related ledger
/// entries go through <see cref="CalculationDiscrepancyLedgerLink"/> for the same reason.
///
/// <see cref="RequiredApprovals"/> defaults to 1 (single reviewer today); turning on dual-approval later
/// is a pure application-logic change (require a second <see cref="CalculationDiscrepancyApproval"/> row
/// with a different reviewer before a transition sticks) — no migration needed.</summary>
public class CalculationDiscrepancy
{
    public long CalculationDiscrepancyId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }
    public CalculationAuditSnapshot? Snapshot { get; set; }

    /// <summary>Null = applies to every dossier variant of this snapshot (the v1 default). Stored as the
    /// enum name (e.g. "SourceRecord") rather than a typed <c>DossierVariant</c> reference — Data.Entities
    /// deliberately never depends on the Services layer, matching every other entity in this project.</summary>
    public string? Variant { get; set; }

    public CalculationDiscrepancySourceType SourceType { get; set; }

    /// <summary>Set when SourceType == Deterministic.</summary>
    public string? OriginCheckKey { get; set; }

    /// <summary>Set when SourceType == AiCandidate. Composite FK to CalculationAiAuditRun's alternate
    /// key, same snapshot-scoping guarantee as PrimaryLedgerEntryId.</summary>
    public long? AiAuditRunId { get; set; }
    public CalculationAiAuditRun? AiAuditRun { get; set; }

    public long PrimaryLedgerEntryId { get; set; }
    public CalculationLedgerEntry? PrimaryLedgerEntry { get; set; }

    public string ClaimSummary { get; set; } = string.Empty;
    public decimal? ClaimedExpectedValue { get; set; }
    public decimal? ClaimedActualValue { get; set; }

    public CalculationDiscrepancyStatus Status { get; set; } = CalculationDiscrepancyStatus.Open;

    /// <summary>Null while Open — an AI-only candidate has no severity until a human triages it, which
    /// mechanically keeps an AI candidate from ever matching the artifact-gate's hold query.</summary>
    public CalculationDiscrepancySeverity? Severity { get; set; }

    public int RequiredApprovals { get; set; } = 1;

    /// <summary>Populated only for Status == AcceptedAsSourceException. Enforced Material-only in code —
    /// there is no route or code path that lets a Critical discrepancy reach this status.</summary>
    public string? ExceptionReason { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime LastUpdatedUtc { get; set; }
}
