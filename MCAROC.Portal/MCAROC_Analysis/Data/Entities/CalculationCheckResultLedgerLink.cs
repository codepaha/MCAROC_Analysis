namespace MCAROC_Analysis.Data.Entities;

/// <summary>Links a <see cref="CalculationCheckResult"/> to the <see cref="CalculationLedgerEntry"/> rows
/// it cites as evidence. Replaces an unconstrained JSON id list on purpose: this row's own
/// <see cref="CalculationAuditSnapshotId"/> participates in a composite foreign key to both the check
/// result and the ledger entry, so the database rejects any attempt to link evidence from a different
/// snapshot than the check result itself — the same evidence-grade guarantee
/// <see cref="CalculationDiscrepancy"/> gets from its own composite FKs.</summary>
public class CalculationCheckResultLedgerLink
{
    public long CalculationCheckResultLedgerLinkId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }

    public long CalculationCheckResultId { get; set; }
    public CalculationCheckResult? CheckResult { get; set; }

    public long CalculationLedgerEntryId { get; set; }
    public CalculationLedgerEntry? LedgerEntry { get; set; }
}
