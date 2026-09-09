namespace MCAROC_Analysis.Data.Entities;

public class Director : ExtractedEntityBase
{
    public long DirectorId { get; set; }

    public string Din { get; set; } = string.Empty;
    public string NameRaw { get; set; } = string.Empty;
    public string NameNormalized { get; set; } = string.Empty;

    public string? Designation { get; set; }
    public DateOnly? DesignationAppointmentDate { get; set; }
    public DateOnly? OriginalAppointmentDate { get; set; }
    public DateOnly? CessationDate { get; set; }

    public string? Flags { get; set; }
}
