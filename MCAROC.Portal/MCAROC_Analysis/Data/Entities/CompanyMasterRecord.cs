namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row of the bulk-imported MCA company/LLP/foreign-company master lists — a static
/// reference table, not request-scoped extracted data. Exists purely so AutoFetch's "search by name"
/// box can resolve a name the caller knows to the identifier (CIN/LLPIN) it needs, without depending on
/// the external reference tool's live search being configured or reachable. Imported and fully replaced
/// by the standalone Tools/ImportCompanyMasterData console tool via SqlBulkCopy — row-by-row EF inserts
/// are not viable at this table's ~3.7M-row scale.</summary>
public sealed class CompanyMasterRecord
{
    /// <summary>CIN, LLPIN, or FCRN exactly as MCA publishes it — the primary key, since the whole
    /// point of this table is "given a name, hand back this identifier".</summary>
    public string Identifier { get; set; } = string.Empty;

    public CompanyMasterRecordType RecordType { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateOnly? RegistrationDate { get; set; }

    /// <summary>Company master only (e.g. "Company limited by shares").</summary>
    public string? Category { get; set; }

    /// <summary>Company master only (e.g. "Private", "Public").</summary>
    public string? Class { get; set; }

    /// <summary>Company master only (e.g. "Listed", "Unlisted").</summary>
    public string? ListingStatus { get; set; }

    public decimal? AuthorizedCapital { get; set; }
    public decimal? PaidupCapital { get; set; }

    public string? Roc { get; set; }
    public string? Address { get; set; }

    /// <summary>Company master only.</summary>
    public string? PinCode { get; set; }

    /// <summary>Company and LLP masters.</summary>
    public string? State { get; set; }

    /// <summary>LLP master only.</summary>
    public string? District { get; set; }

    /// <summary>Foreign-company master only.</summary>
    public string? Country { get; set; }

    /// <summary>e.g. "Active", "Strike Off", "Inactive" — vocabulary differs slightly per master list.</summary>
    public string? Status { get; set; }

    /// <summary>Company master only (e.g. "Non-government company").</summary>
    public string? SubCategory { get; set; }

    public string? IndustrialClassification { get; set; }
}
