namespace MCAROC_Analysis.Data.Entities;

public class AuditorObservation : ExtractedEntityBase
{
    public long ObservationId { get; set; }

    public int FinancialYear { get; set; }

    /// <summary>Standalone ("Auditors' Comments-Standalone") vs Consolidated ("...-Consolidated").
    /// AuditorRules reads Standalone only.</summary>
    public FinancialBasis Basis { get; set; } = FinancialBasis.Standalone;

    /// <summary>Split from the sheet's "Comments Given By" cell: "NAME (Membership Number: X) of FIRM
    /// (Registration Number: Y)". When the text doesn't match that format, AuditorName keeps the raw
    /// text and MembershipNumber/FirmName/FirmRegistrationNumber stay null.</summary>
    public string? AuditorName { get; set; }
    public string? MembershipNumber { get; set; }
    public string? FirmName { get; set; }
    public string? FirmRegistrationNumber { get; set; }

    public bool HasQualificationOrAdverseRemark { get; set; }
    public string? ObservationText { get; set; }

    /// <summary>G18: the detail table's remaining columns (Serial Number / Section / Section Name /
    /// Directors' Comments / Footnotes). A row can exist with ObservationText null when only Directors'
    /// Comments or Footnotes carried real content — see AuditorsParser.</summary>
    public int? SerialNumber { get; set; }
    public string? SectionCode { get; set; }
    public string? SectionName { get; set; }
    public string? DirectorsComments { get; set; }
    public string? Footnotes { get; set; }

    /// <summary>True only for rows parsed from the sheet's second, per-note detail table (Serial Number
    /// | ... | Footnotes). False (the default, including for every row that predates G18) means "table 1,
    /// the year-summary row" — the only kind AuditorRules may treat as the authoritative audit opinion
    /// for a year, since detail rows never carry a meaningful HasQualificationOrAdverseRemark and can now
    /// have a null ObservationText.</summary>
    public bool IsDetailRow { get; set; }
}
