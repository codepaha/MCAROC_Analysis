namespace MCAROC_Analysis.Data.Entities;

/// <summary>A former legal name of the company, from the "NAME HISTORY" block of the "Highlights"
/// sheet. <see cref="TillDate"/> is the date that name was in effect until (the next name took over
/// after it). The current name is not repeated here.</summary>
public sealed class CompanyNameHistory : ExtractedEntityBase
{
    public long CompanyNameHistoryId { get; set; }

    public string PreviousName { get; set; } = string.Empty;

    /// <summary>"Till Date" from the sheet — when this name stopped being used.</summary>
    public DateOnly? TillDate { get; set; }

    /// <summary>Raw "Till Date" text, kept when it does not parse as a date.</summary>
    public string? TillDateRaw { get; set; }

    /// <summary>Row order within the block (oldest name first, as the sheet lists them).</summary>
    public int DisplayOrder { get; set; }
}
