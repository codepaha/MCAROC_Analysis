namespace MCAROC_Analysis.Data.Entities;

public class MsmePayment : ExtractedEntityBase
{
    public long MsmeId { get; set; }

    public string ReportingPeriod { get; set; } = string.Empty;

    public string SupplierNameRaw { get; set; } = string.Empty;
    public string? SupplierPan { get; set; }
    public decimal? AmountDueCrore { get; set; }
}
