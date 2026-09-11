namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Related Corporates" sheet — subsidiaries / associates / JVs and the group
/// exposure they carry. RelationshipType is normalized from the sheet's explicit Relationship text
/// (SUBSIDIARY CORPORATES / ASSOCIATE CORPORATES / ...), never inferred from holding %.</summary>
public class RelatedCorporate : ExtractedEntityBase
{
    public long RelatedCorporateId { get; set; }

    public DateOnly? FinancialYearEnding { get; set; }

    public string EntityNameRaw { get; set; } = string.Empty;
    public string EntityNameNormalized { get; set; } = string.Empty;
    public string? Cin { get; set; }

    public string? RelationshipRaw { get; set; }
    public RelationshipType RelationshipType { get; set; } = RelationshipType.Other;

    public string? CorporateType { get; set; }
    public decimal? HoldingPercent { get; set; }
    public string? Location { get; set; }
    public decimal? PaidUpCapitalCrore { get; set; }
    public decimal? SumOfChargesCrore { get; set; }
    public DateOnly? DateOfIncorporation { get; set; }
    public string? CompanyStatus { get; set; }
    public string? ActiveCompliance { get; set; }
    public string? Remarks { get; set; }
}
