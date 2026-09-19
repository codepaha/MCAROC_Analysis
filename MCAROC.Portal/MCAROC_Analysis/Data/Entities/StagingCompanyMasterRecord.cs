using System;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Heap staging table for raw MCA extract rows loaded via SqlBulkCopy.
/// Insulates live CompanyMasterRecords until full-file validation, collision detection, and checksum checks succeed.
/// </summary>
public sealed class StagingCompanyMasterRecord
{
    public long StagingId { get; set; }

    public long SyncRunId { get; set; }

    public long FencingToken { get; set; }

    public string BatchKey { get; set; } = string.Empty;

    public string? SourceChecksum { get; set; }

    /// <summary>
    /// Integrity state: "Unvalidated", "Validated", "Rejected".
    /// </summary>
    public string ValidationState { get; set; } = "Unvalidated";

    public bool IsPromoted { get; set; }

    public long RowIndex { get; set; }

    // 18 Canonical Entity Properties:
    public string Identifier { get; set; } = string.Empty;

    public CompanyMasterRecordType RecordType { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateOnly? RegistrationDate { get; set; }

    public string? Category { get; set; }

    public string? Class { get; set; }

    public string? ListingStatus { get; set; }

    public decimal? AuthorizedCapital { get; set; }

    public decimal? PaidupCapital { get; set; }

    public string? Roc { get; set; }

    public string? Address { get; set; }

    public string? PinCode { get; set; }

    public string? State { get; set; }

    public string? District { get; set; }

    public string? Country { get; set; }

    public string? Status { get; set; }

    public string? SubCategory { get; set; }

    public string? IndustrialClassification { get; set; }
}
