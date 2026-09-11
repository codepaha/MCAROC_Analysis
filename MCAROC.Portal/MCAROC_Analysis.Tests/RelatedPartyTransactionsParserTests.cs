using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class RelatedPartyTransactionsParserTests
{
    private static SheetData Sample() => Sheet("Related Party Transactions",
        Row("RELATED PARTY TRANSACTIONS"),
        Row("Financial Year Ending On", "Entity Type", "Entity Name", "Relationship", "Transaction Type", "Amount (Rs. Crore)"),
        Row("31 Mar, 2017", "Company", "GRANDEUR POWER PROJECTS PRIVATE LIMITED", "SUBSIDIARY CORPORATES", "Revenue", 12.5),
        Row("31 Mar, 2017", "Company", "GRANDEUR POWER PROJECTS PRIVATE LIMITED", "SUBSIDIARY CORPORATES", "Expense", "-"),
        Row("31 Mar, 2016", "Individual", "SABBINENI SURENDRA", "KEY MANAGERIAL PERSONNEL", "Others", 0.5));

    [Fact]
    public void ParsesOneRowPerPartyTransactionTypeAndYear()
    {
        var r = RelatedPartyTransactionsParser.Parse(Sample(), 1, 1, 10);

        Assert.Equal(3, r.Items.Count);
        Assert.Equal(new DateOnly(2017, 3, 31), r.Items[0].FinancialYearEnding);
        Assert.Equal("Company", r.Items[0].EntityType);
        Assert.Equal("GRANDEUR POWER PROJECTS PRIVATE LIMITED", r.Items[0].EntityNameRaw);
        Assert.Equal("SUBSIDIARY CORPORATES", r.Items[0].RelationshipRaw);
        Assert.Equal("Revenue", r.Items[0].TransactionType);
        Assert.Equal(12.5m, r.Items[0].AmountCrore);

        Assert.Null(r.Items[1].AmountCrore); // "-" is not zero

        Assert.Equal("KEY MANAGERIAL PERSONNEL", r.Items[2].RelationshipRaw);
        Assert.Equal(0.5m, r.Items[2].AmountCrore);

        Assert.All(r.Items, x => Assert.Equal("Related Party Transactions", x.SourceSheetName));
        Assert.Equal(3, r.Items[0].SourceRowNumber);
    }

    [Fact]
    public void MissingHeaderRowWarnsAndProducesNoItems()
    {
        var r = RelatedPartyTransactionsParser.Parse(
            Sheet("Related Party Transactions", Row("RELATED PARTY TRANSACTIONS"), Row("something else")),
            1, 1, 10);

        Assert.Empty(r.Items);
        Assert.Single(r.Warnings, w => w.IssueCode == "RPT_HEADER_NOT_FOUND");
    }

    // ── Codex review (PR #90): the table must have a real boundary, not "scan to end of sheet" ──

    [Fact]
    public void StopsAtABlankRowRatherThanScanningPastIt()
    {
        var sheet = Sheet("Related Party Transactions",
            Row("RELATED PARTY TRANSACTIONS"),
            Row("Financial Year Ending On", "Entity Type", "Entity Name", "Relationship", "Transaction Type", "Amount (Rs. Crore)"),
            Row("31 Mar, 2017", "Company", "GRANDEUR POWER PROJECTS PRIVATE LIMITED", "SUBSIDIARY CORPORATES", "Revenue", 12.5),
            Row(""),
            // An unrelated table further down the sheet whose 3rd column happens to be populated —
            // must never be picked up as more RPT rows.
            Row("Some Other Section"),
            Row("Col A", "Col B", "Col C"),
            Row("x", "y", "NOT A PARTY NAME"));

        var r = RelatedPartyTransactionsParser.Parse(sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("GRANDEUR POWER PROJECTS PRIVATE LIMITED", item.EntityNameRaw);
    }

    [Fact]
    public void StopsAtARepeatedHeaderRatherThanTreatingItAsData()
    {
        var sheet = Sheet("Related Party Transactions",
            Row("RELATED PARTY TRANSACTIONS"),
            Row("Financial Year Ending On", "Entity Type", "Entity Name", "Relationship", "Transaction Type", "Amount (Rs. Crore)"),
            Row("31 Mar, 2017", "Company", "GRANDEUR POWER PROJECTS PRIVATE LIMITED", "SUBSIDIARY CORPORATES", "Revenue", 12.5),
            // No blank separator — the table's own header repeats (e.g. a page-break artifact).
            Row("Financial Year Ending On", "Entity Type", "Entity Name", "Relationship", "Transaction Type", "Amount (Rs. Crore)"),
            Row("31 Mar, 2016", "Company", "SHOULD NOT BE PARSED", "SUBSIDIARY CORPORATES", "Revenue", 1.0));

        var r = RelatedPartyTransactionsParser.Parse(sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("GRANDEUR POWER PROJECTS PRIVATE LIMITED", item.EntityNameRaw);
    }

    [Fact]
    public void StopsAtATotalFooterRow()
    {
        var sheet = Sheet("Related Party Transactions",
            Row("RELATED PARTY TRANSACTIONS"),
            Row("Financial Year Ending On", "Entity Type", "Entity Name", "Relationship", "Transaction Type", "Amount (Rs. Crore)"),
            Row("31 Mar, 2017", "Company", "GRANDEUR POWER PROJECTS PRIVATE LIMITED", "SUBSIDIARY CORPORATES", "Revenue", 12.5),
            Row("", "", "Total", "", "", 12.5));

        var r = RelatedPartyTransactionsParser.Parse(sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("GRANDEUR POWER PROJECTS PRIVATE LIMITED", item.EntityNameRaw);
    }
}
