namespace MCAROC_Analysis.Data.Entities;

/// <summary>Sourced from "Highlights" and "Annexure - Financial Parameters" — a flat key/value list of
/// heterogeneous per-year indicators (employee benefits expense, gross fixed assets, forex income,
/// proposed dividend Yes/No, CSR spend, ...). RawValue is always retained; NumericValue is set only when
/// the cell cleanly parses to a number, otherwise the raw string lives in TextValue — "No" / "Not
/// Applicable" / "-" is never coerced to 0.</summary>
public class FinancialParameter : ExtractedEntityBase
{
    public long FinancialParameterId { get; set; }

    public string ParameterName { get; set; } = string.Empty;
    public int? FinancialYear { get; set; }

    public string RawValue { get; set; } = string.Empty;
    public decimal? NumericValue { get; set; }
    public string? TextValue { get; set; }
    public string? Unit { get; set; }
}
