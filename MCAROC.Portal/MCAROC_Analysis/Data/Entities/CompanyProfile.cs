namespace MCAROC_Analysis.Data.Entities;

public class CompanyProfile : ExtractedEntityBase
{
    public long CompanyProfileId { get; set; }

    public string CompanyName { get; set; } = string.Empty;
    public string? Cin { get; set; }
    public string? Pan { get; set; }
    public string? Llpin { get; set; }

    public string? CompanyStatus { get; set; }
    public string? ComplianceStatus { get; set; }

    public DateOnly? IncorporationDate { get; set; }
    public DateOnly? LastAgmDate { get; set; }

    public string? RegisteredAddress { get; set; }
    public string? RegisteredAddressCity { get; set; }
    public string? RegisteredAddressState { get; set; }
    public string? RegisteredAddressPinCode { get; set; }
    public string? BusinessAddress { get; set; }

    public string? Website { get; set; }
    public string? Phone { get; set; }

    public decimal? AuthorisedCapital { get; set; }
    public decimal? PaidUpCapital { get; set; }
    /// <summary>The MCA-stated "Sum of Charges" from the About sheet — kept alongside our own computed
    /// open-charge total for reconciliation (they can legitimately differ: the MCA figure includes
    /// modifications/history our sum does not).</summary>
    public decimal? McaSumOfChargesCrore { get; set; }

    public string? EntityType { get; set; }
    public string? ListingStatus { get; set; }
    public string? Lei { get; set; }
    public string? LeiStatus { get; set; }

    public string? Industry { get; set; }
    public string? Segment { get; set; }
    public string? BusinessActivity { get; set; }

    /// <summary>The narrative "About the Company" paragraph.</summary>
    public string? NarrativeDescription { get; set; }
}
