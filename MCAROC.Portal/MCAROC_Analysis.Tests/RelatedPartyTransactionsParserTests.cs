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
}
