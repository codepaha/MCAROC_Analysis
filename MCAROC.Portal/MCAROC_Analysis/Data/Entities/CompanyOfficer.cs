namespace MCAROC_Analysis.Data.Entities;

/// <summary>A key managerial person listed on the Directors sheet <em>without</em> a DIN — company
/// secretary, manager, CEO/CFO not holding a directorship. These are genuine officer records (the source
/// simply has no DIN for them), kept here rather than dropped or forced into <see cref="Director"/> with
/// a blank DIN.</summary>
public class CompanyOfficer : ExtractedEntityBase
{
    public long CompanyOfficerId { get; set; }

    public string NameRaw { get; set; } = string.Empty;
    public string NameNormalized { get; set; } = string.Empty;

    public string? Designation { get; set; }
    public DateOnly? DesignationAppointmentDate { get; set; }
    public DateOnly? OriginalAppointmentDate { get; set; }
    public DateOnly? CessationDate { get; set; }

    public string? Flags { get; set; }

    /// <summary>The verbatim contents of the DIN cell that marked this as a non-DIN officer ("-", "NA", …).</summary>
    public string? DinCellRaw { get; set; }
}
