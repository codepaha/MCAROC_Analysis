using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class FinancialDisputeParserTests
{
    private static SheetData Sample() => Sheet("Legal Cases - Financial Dispute",
        Row("Amount Payable / Receivable", "Type Of Financial Dispute", "Currency", "Amount Under Default",
            "Verdict", "Court", "Litigant(s)", "Case No.", "Date Of Default", "Date Of Judgement"),
        Row("Payable", "Trade Payable", "INR", 12.5, "ALLOWED", "NCLT", "ACME BANK vs. TEST CORP", "CP123", "1 Jan, 2020", "5 May, 2021"),
        Row("Receivable", "Trade Receivable", "INR", "-", "PENDING", "HIGH COURT", "TEST CORP vs. OTHER LTD", "WP456", "-", "-"));

    [Fact]
    public void ParsesDirectionAmountAndVerdict()
    {
        var r = FinancialDisputeParser.Parse(Sample(), 1, 1, 10);

        Assert.Equal(2, r.Items.Count);

        Assert.Equal("Payable", r.Items[0].Direction);
        Assert.Equal("Trade Payable", r.Items[0].DisputeType);
        Assert.Equal(12.5m, r.Items[0].AmountUnderDefault);
        Assert.Equal("ALLOWED", r.Items[0].Verdict);
        Assert.Equal("NCLT", r.Items[0].Court);
        Assert.Equal("CP123", r.Items[0].CaseNumber);
        Assert.Equal(new DateOnly(2020, 1, 1), r.Items[0].DateOfDefault);
        Assert.Equal(new DateOnly(2021, 5, 5), r.Items[0].DateOfJudgement);

        Assert.Equal("Receivable", r.Items[1].Direction);
        Assert.Null(r.Items[1].AmountUnderDefault); // "-" is not zero
        Assert.Null(r.Items[1].DateOfDefault); // "-" is not a date
        Assert.Equal("PENDING", r.Items[1].Verdict);

        Assert.All(r.Items, x => Assert.Equal("Legal Cases - Financial Dispute", x.SourceSheetName));
        Assert.Equal(2, r.Items[0].SourceRowNumber);
    }

    [Fact]
    public void MissingHeaderRowWarnsAndProducesNoItems()
    {
        var r = FinancialDisputeParser.Parse(
            Sheet("Legal Cases - Financial Dispute", Row("something else")), 1, 1, 10);

        Assert.Empty(r.Items);
        Assert.Single(r.Warnings, w => w.IssueCode == "FINANCIAL_DISPUTE_HEADER_NOT_FOUND");
    }

    // ── Same table-boundary hardening Codex's review required on the sibling A8/A9 parsers ──

    [Fact]
    public void SkipsARepeatedHeaderAndKeepsParsingBothSidesOfIt()
    {
        var header = Row("Amount Payable / Receivable", "Type Of Financial Dispute", "Currency", "Amount Under Default",
            "Verdict", "Court", "Litigant(s)", "Case No.", "Date Of Default", "Date Of Judgement");
        var sheet = Sheet("Legal Cases - Financial Dispute",
            header,
            Row("Payable", "Trade Payable", "INR", 12.5, "ALLOWED", "NCLT", "ACME BANK vs. TEST CORP", "CP123", "1 Jan, 2020", "5 May, 2021"),
            // No blank separator — the header repeats (a normal page-break/continuation artifact in a
            // paginated export). Rows on both sides are real and must both be kept.
            header,
            Row("Receivable", "Trade Receivable", "INR", 8.0, "PENDING", "HIGH COURT", "TEST CORP vs. OTHER LTD", "WP456", "-", "-"));

        var r = FinancialDisputeParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal("CP123", r.Items[0].CaseNumber);
        Assert.Equal(2, r.Items[0].SourceRowNumber);
        Assert.Equal("WP456", r.Items[1].CaseNumber);
        Assert.Equal(4, r.Items[1].SourceRowNumber); // row 3 is the repeated header, skipped but not counted out
    }

    [Fact]
    public void StopsAtATotalFooterRow()
    {
        var sheet = Sheet("Legal Cases - Financial Dispute",
            Row("Amount Payable / Receivable", "Type Of Financial Dispute", "Currency", "Amount Under Default",
                "Verdict", "Court", "Litigant(s)", "Case No.", "Date Of Default", "Date Of Judgement"),
            Row("Payable", "Trade Payable", "INR", 12.5, "ALLOWED", "NCLT", "ACME BANK vs. TEST CORP", "CP123", "1 Jan, 2020", "5 May, 2021"),
            Row("", "", "", 12.5, "", "", "Total", "", "", ""));

        var r = FinancialDisputeParser.Parse(sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CP123", item.CaseNumber);
    }

    [Fact]
    public void StopsAtABlankRowRatherThanScanningPastIt()
    {
        var sheet = Sheet("Legal Cases - Financial Dispute",
            Row("Amount Payable / Receivable", "Type Of Financial Dispute", "Currency", "Amount Under Default",
                "Verdict", "Court", "Litigant(s)", "Case No.", "Date Of Default", "Date Of Judgement"),
            Row("Payable", "Trade Payable", "INR", 12.5, "ALLOWED", "NCLT", "ACME BANK vs. TEST CORP", "CP123", "1 Jan, 2020", "5 May, 2021"),
            Row(""),
            // An unrelated table further down whose 7th/8th columns happen to be populated.
            Row("Some Other Section"),
            Row("x", "x", "x", "x", "x", "x", "NOT A LITIGANT", "NOT-A-CASE"));

        var r = FinancialDisputeParser.Parse(sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CP123", item.CaseNumber);
    }
}
