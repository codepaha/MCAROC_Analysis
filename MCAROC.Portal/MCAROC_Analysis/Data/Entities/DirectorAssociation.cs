namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Other Directorships" sheet: companies this director is/was associated with.</summary>
public class DirectorAssociation : ExtractedEntityBase
{
    public long AssociationId { get; set; }

    public string DirectorDin { get; set; } = string.Empty;
    public string DirectorNameRaw { get; set; } = string.Empty;

    public string ConnectedCompanyRaw { get; set; } = string.Empty;
    public string ConnectedCompanyNormalized { get; set; } = string.Empty;
    public string? ConnectedCin { get; set; }
    public string? CorporateType { get; set; }

    public decimal? PaidUpCapitalCrore { get; set; }
    public decimal? SumOfChargesCrore { get; set; }

    public DateOnly? DateOfIncorporation { get; set; }
    public string? CompanyStatus { get; set; }
    public string? ActiveCompliance { get; set; }

    public DateOnly? AppointmentDate { get; set; }
    public DateOnly? CessationDate { get; set; }
}
