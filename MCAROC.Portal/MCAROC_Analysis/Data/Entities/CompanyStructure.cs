namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row per ingestion run — the "SHARE HOLDING SUMMARY" block at the top of the
/// "Structure" sheet (promoter/public split and shareholder counts).</summary>
public class CompanyStructure : ExtractedEntityBase
{
    public long CompanyStructureId { get; set; }

    public decimal? PromoterHoldingPercent { get; set; }
    public decimal? PublicHoldingPercent { get; set; }
    public int? TotalShareholders { get; set; }
    public int? PromoterShareholders { get; set; }
    public long? TotalEquityShares { get; set; }
    public long? TotalPreferenceShares { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string? Cin { get; set; }
}
