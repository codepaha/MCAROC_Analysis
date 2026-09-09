namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from the "Securities Allotment" sheet — the company's capital-raise history
/// (date, cash/other-than-cash, instrument, amount, count, nominal/premium value).</summary>
public class SecurityAllotment : ExtractedEntityBase
{
    public long SecurityAllotmentId { get; set; }

    public DateOnly? AllotmentDate { get; set; }
    public string? AllotmentType { get; set; }
    public string? InstrumentType { get; set; }

    public decimal? AmountCrore { get; set; }
    public long? NumberOfSecurities { get; set; }
    public decimal? NominalValuePerShare { get; set; }
    public decimal? PremiumValuePerShare { get; set; }
}
