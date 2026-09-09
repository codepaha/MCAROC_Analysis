namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Director - Association History" sheet — each row is one designation
/// "stint" a director held at <em>this</em> company, with its appointment and cessation dates. A
/// director can appear multiple times (e.g. Managing Director then Director then Additional Director).
/// Distinct from "Other Directorships" (DirectorAssociation), which is other companies.</summary>
public class DirectorAssignmentHistory : ExtractedEntityBase
{
    public long DirectorAssignmentHistoryId { get; set; }

    public string DirectorDin { get; set; } = string.Empty;
    public string DirectorNameRaw { get; set; } = string.Empty;

    public string? Designation { get; set; }
    public DateOnly? AppointmentDate { get; set; }
    public DateOnly? CessationDate { get; set; }
}
