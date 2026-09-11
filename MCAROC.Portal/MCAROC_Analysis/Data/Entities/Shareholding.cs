namespace MCAROC_Analysis.Data.Entities;

/// <summary>Merged from "Director Shareholding" and "Shareholding More Than 5%", deduplicated by (name, year).</summary>
public class Shareholding : ExtractedEntityBase
{
    public long ShareholdingId { get; set; }

    public int FinancialYear { get; set; }

    public string ShareholderNameRaw { get; set; } = string.Empty;
    public string ShareholderNameNormalized { get; set; } = string.Empty;
    public string? ShareholderType { get; set; }

    public long? SharesHeld { get; set; }
    public decimal? HoldingPercentage { get; set; }

    public bool IsPromoter { get; set; }

    /// <summary>Which source sheet(s) this row was found in; a person can appear in both.</summary>
    public ShareholdingSourceType SourceType { get; set; }

    // Director Shareholding only.
    public string? Designation { get; set; }
    public DateOnly? CessationDate { get; set; }

    // Shareholding More Than 5% only. RelationshipRaw is the sheet's own "Relationship" column
    // (e.g. SHAREHOLDER / PROMOTER) — distinct from ShareholderType, which is its "Entity Type" column.
    public string? RelationshipRaw { get; set; }
    public string? Location { get; set; }
    public decimal? PaidUpCapitalCrore { get; set; }
    public decimal? SumOfChargesCrore { get; set; }
    public DateOnly? DateOfIncorporation { get; set; }
    public string? CompanyStatus { get; set; }
    public string? ActiveCompliance { get; set; }
    public string? Remarks { get; set; }
}
