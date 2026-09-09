namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from "Annexure - GST" — per-return filing history for a GSTIN.</summary>
public class GstFiling : ExtractedEntityBase
{
    public long GstFilingId { get; set; }

    public long GstRegistrationId { get; set; }
    public string Gstin { get; set; } = string.Empty;

    public string ReturnType { get; set; } = string.Empty;
    public string? FinancialYear { get; set; }
    public string? TaxPeriod { get; set; }

    public DateOnly? DueDate { get; set; }
    public DateOnly? FilingDate { get; set; }

    public string? FilingStatus { get; set; }
    public int? DelayDays { get; set; }
}
