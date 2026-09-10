namespace MCAROC_Analysis.Data.Entities;

public enum ShareholderClass { Promoter, Public }

/// <summary>One category row of the "Structure" sheet's two SEBI-category grids — <c>PROMOTERS</c> and
/// <c>PUBLIC / OTHER THAN PROMOTERS</c> — each listing the 10 numbered shareholder categories (with
/// categories 1 and 2 split into their (i)/(ii)/(iii) sub-rows) against {equity shares, equity %,
/// preference shares, preference %}. Verbatim <see cref="Category"/>; a sub-row keeps its parent in
/// <see cref="CategoryGroup"/>. The parent-only header rows and the per-grid "Total" line are not
/// stored — <see cref="CompanyStructure"/> already carries the class totals, and Layer 0
/// <see cref="SourceRow"/> keeps the sheet verbatim.</summary>
public sealed class ShareholdingPatternRow : ExtractedEntityBase
{
    public long ShareholdingPatternRowId { get; set; }

    public ShareholderClass HolderClass { get; set; }

    /// <summary>The "as on" date from the grid header ("PROMOTERS - 31 Mar, 2017").</summary>
    public DateOnly? AsOnDate { get; set; }

    /// <summary>Verbatim category label, e.g. "(i) Indian", "3. Insurance companies", "10. Others".</summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>For a sub-row, the numbered parent it sits under ("1. Individual / Hindu Undivided
    /// Family"); null for a top-level numbered category.</summary>
    public string? CategoryGroup { get; set; }

    /// <summary>1-based position of this row within its grid, in sheet order.</summary>
    public int DisplayOrder { get; set; }

    public long? EquityShares { get; set; }
    public decimal? EquityPercent { get; set; }
    public long? PreferenceShares { get; set; }
    public decimal? PreferencePercent { get; set; }
}
