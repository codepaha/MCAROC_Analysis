namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Compliance" sheet — MCA name removal/restoration, BIFR, CDR (corporate
/// debt restructuring), and CIBIL suit-filed / wilful-defaulter records. CDR and suit-filed entries in
/// particular are highly material to a lender review.</summary>
public class ComplianceRecord : ExtractedEntityBase
{
    public long ComplianceRecordId { get; set; }

    public ComplianceRecordType RecordType { get; set; }
    public string? Description { get; set; }
    public DateOnly? RecordDate { get; set; }
    public string? Status { get; set; }

    // Suit-filed / wilful-defaulter rows carry structured columns.
    public string? Source { get; set; }        // e.g. "CIBIL"
    public string? Bank { get; set; }
    public decimal? AmountCrore { get; set; }
    public string? DefaulterType { get; set; }  // "Defaulter - Suit Filed" / "Wilful Defaulter - Non Suit Filed"

    /// <summary>The verbatim sheet cell(s) this record was read from — always kept for traceability.</summary>
    public string? SourceText { get; set; }
}
