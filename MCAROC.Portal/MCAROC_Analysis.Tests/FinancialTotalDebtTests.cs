using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The financial sheet lists long- and short-term borrowings but no total line, so Total Debt was never filled: every dossier showed
/// "—" and the High Leverage rule could never run (found in the TruBoard test, 2026-10-13).</summary>
public class FinancialTotalDebtTests
{
    private static SheetData Sheet(params object?[][] lines) =>
        new("Standalone Financial Data", [new object?[] { "BALANCE SHEET (Rs. Crore)", null, "2025", "2024" }, .. lines]);

    [Fact]
    public void Total_debt_is_the_sum_of_long_and_short_term_borrowings()
    {
        var result = StandaloneFinancialDataParser.Parse(
            Sheet(new object?[] { "Long Term Borrowings", null, "27.69", "25.04" }, new object?[] { "Short Term Borrowings", null, "1.50", "0.00" }),
            1, 1, null, out _);

        var fy2025 = Assert.Single(result.Items, f => f.FinancialYear == 2025);
        Assert.Equal(29.19m, fy2025.TotalDebt);
        Assert.Equal(25.04m, Assert.Single(result.Items, f => f.FinancialYear == 2024).TotalDebt);
    }

    [Fact]
    public void Total_debt_stays_empty_when_neither_borrowing_line_is_reported()
    {
        var result = StandaloneFinancialDataParser.Parse(Sheet(new object?[] { "Share Capital", null, "0.25", "0.23" }), 1, 1, null, out _);
        Assert.All(result.Items, f => Assert.Null(f.TotalDebt));
    }

    [Theory]
    [InlineData(27.69, null, 27.69)]
    [InlineData(null, 3.0, 3.0)]
    [InlineData(null, null, null)]
    public void One_reported_line_is_enough(double? longTerm, double? shortTerm, double? expected) =>
        Assert.Equal((decimal?)expected, StandaloneFinancialDataParser.TotalDebtOf((decimal?)longTerm, (decimal?)shortTerm));
}
