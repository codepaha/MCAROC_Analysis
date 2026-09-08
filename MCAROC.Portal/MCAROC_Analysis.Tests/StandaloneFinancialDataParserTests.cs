using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class StandaloneFinancialDataParserTests
{
    [Fact]
    public void AlignsBalanceSheetAndPnlToTheSameYearHeader()
    {
        var sheet = Sheet("Standalone Financial Data",
            Row("BALANCE SHEET - AOC-4 (Rs. Crore)", "", "31 Mar, 2024", "31 Mar, 2025"),
            Row("Share Capital", "", 2.13, 2.11),
            Row("Total Equity", "", -13.34, -11.23),
            Row("PROFIT & LOSS - AOC-4 (Rs. Crore)", "", "31 Mar, 2024", "31 Mar, 2025"),
            Row("Net Revenue", "", 118.74, 20.25),
            Row("Profit for the Period", "", -242.51, -103.92));

        var result = StandaloneFinancialDataParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, result.Items.Count);
        var fy2025 = result.Items.Single(f => f.FinancialYear == 2025);
        Assert.Equal(2.11m, fy2025.ShareCapital);
        Assert.Equal(-11.23m, fy2025.NetWorth);
        Assert.Equal(20.25m, fy2025.Revenue);
        Assert.Equal(-103.92m, fy2025.Pat);
    }

    [Fact]
    public void WarnsAndMapsCashFlowToMostRecentYearsWhenItReportsFewerColumns()
    {
        var sheet = Sheet("Standalone Financial Data",
            Row("BALANCE SHEET - AOC-4 (Rs. Crore)", "", "31 Mar, 2023", "31 Mar, 2024", "31 Mar, 2025"),
            Row("Share Capital", "", 1.0, 2.0, 3.0),
            Row("CASH FLOW - AOC-4 (Rs. Crore)", ""),
            Row("Cash Flows from / ( Used in ) Operating Activities", ""),
            Row("Net Cash Flows from / ( Used in ) Operating Activities", "", -5.0, -6.0)); // only 2 of 3 years

        var result = StandaloneFinancialDataParser.Parse(sheet, 1, 1, 10);

        Assert.Single(result.Warnings, w => w.IssueCode == "CASH_FLOW_YEAR_ALIGNMENT_ASSUMED");

        // 2 populated columns map to the *last* 2 years of the 3-year master list: 2024 and 2025.
        var fy2024 = result.Items.Single(f => f.FinancialYear == 2024);
        var fy2025 = result.Items.Single(f => f.FinancialYear == 2025);
        Assert.Equal(-5.0m, fy2024.Cfo);
        Assert.Equal(-6.0m, fy2025.Cfo);
        Assert.DoesNotContain(result.Items, f => f.FinancialYear == 2023 && f.Cfo != null);
    }
}
