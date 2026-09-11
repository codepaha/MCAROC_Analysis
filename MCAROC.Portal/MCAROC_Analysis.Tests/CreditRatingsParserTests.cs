using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class CreditRatingsParserTests
{
    private static SheetData CreditRatingsSheet(string agencyHeader = "AGENCY") => Sheet("Credit Ratings",
        Row(agencyHeader, "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
        Row("CRISIL", "15 Jun, 2020", "Long Term Bank Facilities", 150.0, "INR", "CRISIL A", "Reaffirmed", "Stable", "-"),
        Row("ICRA", "1 Jan, 2021", "Cash Credit", "-", "INR", "-", "Withdrawn", "-", "At the request of the issuer"));

    private static SheetData UnacceptedRatingsSheet() => Sheet("Unaccepted Ratings",
        Row("AGENCY", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "DATE OF NON-ACCEPTANCE", "REMARKS"),
        Row("CARE", "Term Loan", 50.0, "INR", "CARE BB+", "10 Mar, 2019", "-"));

    [Fact]
    public void ParsesAcceptedRatingsWithActionAndOutlook()
    {
        var r = CreditRatingsParser.Parse(CreditRatingsSheet(), null, 1, 1, 10);

        Assert.Equal(2, r.Items.Count);
        Assert.All(r.Items, x => Assert.True(x.IsAccepted));
        Assert.Equal("CRISIL", r.Items[0].Agency);
        Assert.Equal(new DateOnly(2020, 6, 15), r.Items[0].RatingDate);
        Assert.Equal(150.0m, r.Items[0].Amount);
        Assert.Equal("Reaffirmed", r.Items[0].Action);
        Assert.Equal("Stable", r.Items[0].Outlook);

        // RATING can be "-" (e.g. when ACTION = Withdrawn) — not fabricated.
        Assert.Null(r.Items[1].Rating);
        Assert.Null(r.Items[1].Amount);
        Assert.Equal("Withdrawn", r.Items[1].Action);
    }

    [Fact]
    public void TolerastesTrailingSpaceHeader()
    {
        var r = CreditRatingsParser.Parse(CreditRatingsSheet(agencyHeader: "AGENCY "), null, 1, 1, 10);
        Assert.Equal(2, r.Items.Count);
    }

    [Fact]
    public void ParsesUnacceptedRatingsFromASeparateSheetWithIsAcceptedFalse()
    {
        var r = CreditRatingsParser.Parse(null, UnacceptedRatingsSheet(), 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.False(item.IsAccepted);
        Assert.Equal("CARE", item.Agency);
        Assert.Equal("Term Loan", item.Instrument);
        Assert.Equal(50.0m, item.Amount);
        Assert.Equal(new DateOnly(2019, 3, 10), item.RatingDate); // Date Of Non-Acceptance -> RatingDate
        Assert.Null(item.Action); // Unaccepted layout carries no Action column
    }

    [Fact]
    public void ParsesAnEmbeddedUnacceptedRatingsBlockWithinTheSameSheet()
    {
        var sheet = Sheet("Credit Ratings",
            Row("AGENCY", "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
            Row("CRISIL", "15 Jun, 2020", "Long Term Bank Facilities", 150.0, "INR", "CRISIL A", "Reaffirmed", "Stable", "-"),
            Row(""),
            Row("UNACCEPTED RATINGS"),
            Row("AGENCY", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "DATE OF NON-ACCEPTANCE", "REMARKS"),
            Row("CARE", "Term Loan", 50.0, "INR", "CARE BB+", "10 Mar, 2019", "-"));

        var r = CreditRatingsParser.Parse(sheet, null, 1, 1, 10);

        Assert.Equal(2, r.Items.Count);
        Assert.True(r.Items[0].IsAccepted);
        Assert.False(r.Items[1].IsAccepted);
        Assert.Equal("CARE", r.Items[1].Agency);
    }

    [Fact]
    public void MissingHeaderRowWarns()
    {
        var r = CreditRatingsParser.Parse(Sheet("Credit Ratings", Row("something else")), null, 1, 1, 10);
        Assert.Empty(r.Items);
        Assert.Single(r.Warnings, w => w.IssueCode == "CREDIT_RATINGS_HEADER_NOT_FOUND");
    }

    // ── Codex review (PR #91): the table must have a real boundary, not "scan to end of sheet" ──

    [Fact]
    public void StopsAtARepeatedHeaderRatherThanTreatingItAsData()
    {
        var sheet = Sheet("Credit Ratings",
            Row("AGENCY", "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
            Row("CRISIL", "15 Jun, 2020", "Long Term Bank Facilities", 150.0, "INR", "CRISIL A", "Reaffirmed", "Stable", "-"),
            // No blank separator — the header repeats (e.g. a page-break artifact).
            Row("AGENCY", "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
            Row("SHOULD NOT BE PARSED", "1 Jan, 2021", "x", 1.0, "INR", "x", "x", "x", "x"));

        var r = CreditRatingsParser.Parse(sheet, null, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CRISIL", item.Agency);
    }

    [Fact]
    public void StopsAtATotalFooterRow()
    {
        var sheet = Sheet("Credit Ratings",
            Row("AGENCY", "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
            Row("CRISIL", "15 Jun, 2020", "Long Term Bank Facilities", 150.0, "INR", "CRISIL A", "Reaffirmed", "Stable", "-"),
            Row("Total", "", "", 150.0, "", "", "", "", ""));

        var r = CreditRatingsParser.Parse(sheet, null, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CRISIL", item.Agency);
    }

    [Fact]
    public void StopsAtAnUnrelatedRowRatherThanScanningPastAGap()
    {
        var sheet = Sheet("Credit Ratings",
            Row("AGENCY", "DATE", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "ACTION", "OUTLOOK", "REMARKS"),
            Row("CRISIL", "15 Jun, 2020", "Long Term Bank Facilities", 150.0, "INR", "CRISIL A", "Reaffirmed", "Stable", "-"),
            Row(""),
            // An unrelated section further down the sheet whose 1st column happens to be populated —
            // must never be picked up as more ratings.
            Row("Some Other Section"),
            Row("NOT AN AGENCY", "x", "y"));

        var r = CreditRatingsParser.Parse(sheet, null, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CRISIL", item.Agency);
    }

    [Fact]
    public void UnacceptedRatingsStopsAtARepeatedHeaderAndATotalRow()
    {
        var sheet = Sheet("Unaccepted Ratings",
            Row("AGENCY", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "DATE OF NON-ACCEPTANCE", "REMARKS"),
            Row("CARE", "Term Loan", 50.0, "INR", "CARE BB+", "10 Mar, 2019", "-"),
            Row("AGENCY", "INSTRUMENT", "AMOUNT", "CURRENCY", "RATING", "DATE OF NON-ACCEPTANCE", "REMARKS"),
            Row("SHOULD NOT BE PARSED", "x", 1.0, "INR", "x", "1 Jan, 2020", "x"));

        var r = CreditRatingsParser.Parse(null, sheet, 1, 1, 10);

        var item = Assert.Single(r.Items);
        Assert.Equal("CARE", item.Agency);
    }
}
