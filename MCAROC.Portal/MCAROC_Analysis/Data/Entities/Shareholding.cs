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
}
