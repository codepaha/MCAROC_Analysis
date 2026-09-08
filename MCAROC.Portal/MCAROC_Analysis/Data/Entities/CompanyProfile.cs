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
    public string? RegisteredAddress { get; set; }

    public decimal? AuthorisedCapital { get; set; }
    public decimal? PaidUpCapital { get; set; }

    public string? Industry { get; set; }
    public string? BusinessActivity { get; set; }
}
