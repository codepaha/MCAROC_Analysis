using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>One line item in a financial statement (P&amp;L, Balance Sheet, Cash Flow, or Ratios)
/// displaying across multiple financial years.</summary>
public record FinancialStatementRow
{
    public string Label { get; init; } = string.Empty;
    public bool IsSubtotal { get; init; }
    public bool IsHeader { get; init; }
    public int? SourceRowNumber { get; init; }
    public Dictionary<int, (decimal? Numeric, string? Raw, bool YearInferred)> ValuesByYear { get; init; } = new();

    public (decimal? Numeric, string? Raw, bool YearInferred) GetValue(int year) =>
        ValuesByYear.TryGetValue(year, out var val) ? val : (null, null, false);
}

/// <summary>Represents the data for one reporting basis (Standalone or Consolidated).</summary>
public record FinancialStatementViewData(
    List<int> Years,
    List<FinancialStatementRow> Rows,
    bool HasYearInferred,
    string Unit = "₹ Crore")
{
    public bool HasData => Years.Count > 0 && Rows.Count > 0;
}

/// <summary>Feeds <c>Details/_FinancialStatement.cshtml</c> — one financial statement (P&amp;L / Balance
/// Sheet / Cash Flow / Ratios) with full statement line items and a Standalone / Consolidated toggle.</summary>
public record FinancialStatementViewModel(
    string Key,
    string Title,
    FinancialStatementViewData Standalone,
    FinancialStatementViewData Consolidated,
    string? Description = null)
{
    public bool HasConsolidated => Consolidated.HasData;
    public bool HasAnyData => Standalone.HasData || Consolidated.HasData;
    public bool HasYearInferred => Standalone.HasYearInferred || Consolidated.HasYearInferred;
}
