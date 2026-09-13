namespace MCAROC_Analysis.Data.Entities;

/// <summary>The one place the (Request, IngestionRun, AnalysisRun) tuple lives for #164's calculation
/// assurance feature. Every other calculation-assurance table references this row's id instead of
/// repeating the raw tuple, and composite foreign keys anchored on <see cref="CalculationAuditSnapshotId"/>
/// make it impossible at the database level for a discrepancy, hold, or check-evidence link to cite a
/// ledger entry from a different snapshot. Created (get-or-create) the first time
/// CalculationLedgerService persists a ledger for a given tuple — never updated afterwards.</summary>
public class CalculationAuditSnapshot
{
    public long CalculationAuditSnapshotId { get; set; }

    public long RequestId { get; set; }
    public long IngestionRunId { get; set; }
    public long AnalysisRunId { get; set; }

    public DateTime CreatedUtc { get; set; }
}
