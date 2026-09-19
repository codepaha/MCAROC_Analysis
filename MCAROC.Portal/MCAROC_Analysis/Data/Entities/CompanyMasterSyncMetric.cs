using System;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Breakdown of delta statistics (new additions, existing updates, unchanged skips, skipped corrupted rows)
/// per record type (Company, Llp, Foreign) for an ingested Company Master snapshot.
/// </summary>
public sealed class CompanyMasterSyncMetric
{
    public long MetricId { get; set; }

    public long JobId { get; set; }
    public CompanyMasterSyncJob? Job { get; set; }

    public CompanyMasterRecordType RecordType { get; set; }

    public int TotalSourceRows { get; set; }
    public int NewRowsAdded { get; set; }
    public int ExistingRowsUpdated { get; set; }
    public int UnchangedRowsSkipped { get; set; }
    public int CorruptedRowsSkipped { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
