namespace MCAROC_Analysis.Data.Entities;

/// <summary>One row of the "PRINCIPAL BUSINESS ACTIVITIES" block of the "Highlights" sheet — the MCA
/// activity classification the company declared, with the share of turnover it represents. A company
/// can declare several (they sum to ~100%).</summary>
public sealed class PrincipalBusinessActivity : ExtractedEntityBase
{
    public long PrincipalBusinessActivityId { get; set; }

    /// <summary>The "as on" date from the block header ("PRINCIPAL BUSINESS ACTIVITIES - 31 Mar, 2017").</summary>
    public DateOnly? AsOnDate { get; set; }

    public string? MainActivityGroupCode { get; set; }
    public string? MainActivityGroupDescription { get; set; }
    public string? BusinessActivityCode { get; set; }
    public string? BusinessActivityDescription { get; set; }
    public decimal? TurnoverPercent { get; set; }

    public int DisplayOrder { get; set; }
}
