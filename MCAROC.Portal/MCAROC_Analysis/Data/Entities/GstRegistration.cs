namespace MCAROC_Analysis.Data.Entities;

public class GstRegistration : ExtractedEntityBase
{
    public long GstId { get; set; }

    public string Gstin { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? Status { get; set; }

    public DateOnly? RegistrationDate { get; set; }
    public DateOnly? CancellationDate { get; set; }

    public string? TaxpayerType { get; set; }
    public string? TradeName { get; set; }
    public string? NatureOfBusinessActivities { get; set; }
    public string? Flags { get; set; }

    public List<GstFiling> Filings { get; set; } = [];
}
