using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>Feeds <c>Details/_FinancialStatement.cshtml</c> — one financial statement (P&amp;L / Balance
/// Sheet / Cash Flow / Summary) rendered from <see cref="FinancialYearData"/> with a Standalone /
/// Consolidated toggle. The toggle is shown only when <see cref="Consolidated"/> has rows.</summary>
public record FinancialStatementViewModel(
    string Key,
    string Title,
    IReadOnlyList<FinancialStatementRow> Rows,
    List<FinancialYearData> Standalone,
    List<FinancialYearData> Consolidated)
{
    public bool HasConsolidated => Consolidated.Count > 0;
}

/// <summary>One line item — a label and a selector pulling the value off a <see cref="FinancialYearData"/> row.</summary>
public record FinancialStatementRow(string Label, Func<FinancialYearData, decimal?> Value);
