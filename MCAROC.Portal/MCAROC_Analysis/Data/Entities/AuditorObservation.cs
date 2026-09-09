namespace MCAROC_Analysis.Data.Entities;

public class AuditorObservation : ExtractedEntityBase
{
    public long ObservationId { get; set; }

    public int FinancialYear { get; set; }

    /// <summary>Standalone ("Auditors' Comments-Standalone") vs Consolidated ("...-Consolidated").
    /// AuditorRules reads Standalone only.</summary>
    public FinancialBasis Basis { get; set; } = FinancialBasis.Standalone;

    public string? AuditorName { get; set; }

    public bool HasQualificationOrAdverseRemark { get; set; }
    public string? ObservationText { get; set; }
}
