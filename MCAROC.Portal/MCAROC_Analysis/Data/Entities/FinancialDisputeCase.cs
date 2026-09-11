namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Legal Cases - Financial Dispute" sheet — money-claim disputes with an
/// amount-under-default and a verdict. A separate register from "Legal History"; never merged with
/// <see cref="Litigation"/> rows.</summary>
public class FinancialDisputeCase : ExtractedEntityBase
{
    public long FinancialDisputeCaseId { get; set; }

    /// <summary>"AMOUNT PAYABLE" or "AMOUNT RECEIVABLE" — a direction flag, not a number. Kept verbatim;
    /// the number itself is <see cref="AmountUnderDefault"/>.</summary>
    public string? Direction { get; set; }

    public string? DisputeType { get; set; }
    public string? Currency { get; set; }
    public decimal? AmountUnderDefault { get; set; }
    public string? Verdict { get; set; }
    public string? Court { get; set; }
    public string? Litigants { get; set; }
    public string? CaseNumber { get; set; }
    public DateOnly? DateOfDefault { get; set; }
    public DateOnly? DateOfJudgement { get; set; }
}
