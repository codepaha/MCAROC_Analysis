namespace MCAROC_Analysis.Data.Entities;

public class AuditorObservation : ExtractedEntityBase
{
    public long ObservationId { get; set; }

    public int FinancialYear { get; set; }
    public string? AuditorName { get; set; }

    public bool HasQualificationOrAdverseRemark { get; set; }
    public string? ObservationText { get; set; }
}
