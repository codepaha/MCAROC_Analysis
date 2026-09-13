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
}
