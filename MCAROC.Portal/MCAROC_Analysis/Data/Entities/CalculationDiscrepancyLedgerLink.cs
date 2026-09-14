namespace MCAROC_Analysis.Data.Entities;

/// <summary>Non-primary ledger entries a <see cref="CalculationDiscrepancy"/> cites as related evidence.
/// Same pattern as <see cref="CalculationCheckResultLedgerLink"/>: this row's own
/// <see cref="CalculationAuditSnapshotId"/> participates in composite FKs to both the discrepancy and the
/// ledger entry, so a "related" reference can never point at a different snapshot than the discrepancy's
/// own <see cref="CalculationDiscrepancy.PrimaryLedgerEntryId"/> already does.</summary>
public class CalculationDiscrepancyLedgerLink
{
    public long CalculationDiscrepancyLedgerLinkId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }

    public long CalculationDiscrepancyId { get; set; }
    public CalculationDiscrepancy? Discrepancy { get; set; }

    public long CalculationLedgerEntryId { get; set; }
    public CalculationLedgerEntry? LedgerEntry { get; set; }
}
