namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from "Annexure - EPFO Establishments" — per-wage-month contribution history.</summary>
public class EpfoContribution : ExtractedEntityBase
{
    public long EpfoId { get; set; }

    public string EstablishmentId { get; set; } = string.Empty;
    public string? EstablishmentName { get; set; }
    public string? WorkingStatus { get; set; }

    public string WageMonth { get; set; } = string.Empty;

    /// <summary>Transaction reference number for this month's remittance (the annexure's TRRN column).</summary>
    public string? Trrn { get; set; }

    public int? EmployeeCount { get; set; }
    public decimal? ContributionAmountCrore { get; set; }

    public DateOnly? PaymentDueDate { get; set; }
    public DateOnly? PaymentDate { get; set; }

    public string? PaymentStatus { get; set; }
}
