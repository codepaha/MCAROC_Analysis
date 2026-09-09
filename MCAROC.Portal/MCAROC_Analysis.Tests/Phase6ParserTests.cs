using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Excel.Parsers;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class StructureParserTests
{
    [Fact]
    public void ParsesShareHoldingSummary()
    {
        var sheet = Sheet("Structure",
            Row("SHARE HOLDING SUMMARY"),
            Row("Promoter %", 11.44),
            Row("Public %", 88.56),
            Row("No. of Shareholders", 37.0),
            Row("No. of Promoter Shareholders", 5.0),
            Row("Total Equity Shares", 330935600.0),
            Row("Total Preference Shares", 0.0));

        var r = StructureParser.Parse(sheet, 1, 1, 10);

        var s = Assert.Single(r.Items);
        Assert.Equal(11.44m, s.PromoterHoldingPercent);
        Assert.Equal(88.56m, s.PublicHoldingPercent);
        Assert.Equal(37, s.TotalShareholders);
        Assert.Equal(5, s.PromoterShareholders);
        Assert.Equal(330935600L, s.TotalEquityShares);
    }

    [Fact]
    public void EmptySummary_WarnsAndProducesNoRow()
    {
        var r = StructureParser.Parse(Sheet("Structure", Row("SHARE HOLDING SUMMARY"), Row("Something else", "x")), 1, 1, 10);
        Assert.Empty(r.Items);
        Assert.Single(r.Warnings, w => w.IssueCode == "STRUCTURE_SUMMARY_EMPTY");
    }
}

public class RelatedCorporatesParserTests
{
    private static SheetData Sample() => Sheet("Related Corporates",
        Row("RELATED CORPORATES"),
        Row("Financial Year Ending On", "Corporate Name", "Relationship", "Corporate Type", "Shareholding (%)",
            "Country / City", "Paid Up Capital", "Sum Of Charges", "Date Of Incorporation", "Company / LLP Status", "Active Compliance", "Remarks"),
        Row("31 Mar, 2017", "JALPOWER CORPORATION LIMITED", "SUBSIDIARY CORPORATES", "COMPANY", 50.18,
            "Hyderabad", 533.1, 1279.0, "25 Aug, 2004", "Active", "Active Compliant", "-"),
        Row("31 Mar, 2017", "NEPALJAL", "ASSOCIATE CORPORATES", "OTHERS", 41.0, "-", "-", "-", "-", "-", "-", "-"),
        Row("31 Mar, 2017", "NUZIVEEDU JV", "JOINT VENTURE", "OTHERS", 0.0, "INDIA", "-", "-", "-", "-", "-", "-"));

    [Fact]
    public void NormalizesRelationshipFromExplicitTextOnly()
    {
        var r = RelatedCorporatesParser.Parse(Sample(), 1, 1, 10);

        Assert.Equal(3, r.Items.Count);
        Assert.Equal(RelationshipType.Subsidiary, r.Items[0].RelationshipType);
        Assert.Equal(RelationshipType.Associate, r.Items[1].RelationshipType);
        Assert.Equal(RelationshipType.JointVenture, r.Items[2].RelationshipType);
        Assert.Equal(533.1m, r.Items[0].PaidUpCapitalCrore);
        Assert.Equal(1279.0m, r.Items[0].SumOfChargesCrore);
        Assert.Null(r.Items[1].PaidUpCapitalCrore); // "-" is not zero
    }
}

public class ComplianceParserTests
{
    [Fact]
    public void ParsesCdrAndSuitFiledSections()
    {
        var sheet = Sheet("Compliance",
            Row("INCIDENTS OF NAME REMOVAL U/S 248"),
            Row("As per our records, this corporate is not struck off"),
            Row("BIFR"),
            Row("This corporate has no BIFR Case"),
            Row("CDR"),
            Row("Description", "Date"),
            Row("As Per Directors Report 2017, debt was restructured under CDR", "28 Apr, 2014"),
            Row("SUIT FILED CASES"),
            Row("Source", "Bank", "Date", "Amount (Rs. Crore)", "Defaulter Type"),
            Row("CIBIL", "CENTRAL BANK OF INDIA", "31 Dec, 2013", 56.09, "Defaulter - Suit Filed"),
            Row("CIBIL", "BANK OF BAHRAIN", "31 Mar, 2015", 5.4, "Wilful Defaulter - Non Suit Filed"));

        var r = ComplianceParser.Parse(sheet, 1, 1, 10);

        var cdr = Assert.Single(r.Items, x => x.RecordType == ComplianceRecordType.Cdr);
        Assert.Equal(new DateOnly(2014, 4, 28), cdr.RecordDate);

        var suits = r.Items.Where(x => x.RecordType == ComplianceRecordType.SuitFiled).ToList();
        Assert.Equal(2, suits.Count);
        Assert.Equal("CENTRAL BANK OF INDIA", suits[0].Bank);
        Assert.Equal(56.09m, suits[0].AmountCrore);
        Assert.Equal("Wilful Defaulter - Non Suit Filed", suits[1].DefaulterType);

        Assert.DoesNotContain(r.Items, x => x.RecordType == ComplianceRecordType.NameRemoval);
        Assert.DoesNotContain(r.Items, x => x.RecordType == ComplianceRecordType.Bifr);
    }

    [Fact]
    public void KeepsEverySuitFiledQuarter_NoLongerCollapsesToLatest()
    {
        // CIBIL re-reports the same default every quarter — all reported quarters are kept now.
        var sheet = Sheet("Compliance",
            Row("SUIT FILED CASES"),
            Row("Source", "Bank", "Date", "Amount (Rs. Crore)", "Defaulter Type"),
            Row("CIBIL", "IDBI BANK", "31 Mar, 2014", 12.0, "Defaulter - Suit Filed"),
            Row("CIBIL", "IDBI BANK", "30 Jun, 2014", 12.0, "Defaulter - Suit Filed"),
            Row("CIBIL", "IDBI BANK", "30 Sep, 2014", 12.0, "Defaulter - Suit Filed"));

        var r = ComplianceParser.Parse(sheet, 1, 1, 10);

        var suits = r.Items.Where(x => x.RecordType == ComplianceRecordType.SuitFiled).ToList();
        Assert.Equal(3, suits.Count);
        Assert.Equal(
            [new DateOnly(2014, 3, 31), new DateOnly(2014, 6, 30), new DateOnly(2014, 9, 30)],
            suits.Select(s => s.RecordDate).ToArray());
    }
}

public class FinancialParametersParserTests
{
    [Fact]
    public void KeepsRawValue_NeverCoercesTextToZero()
    {
        var annexure = Sheet("Annexure - Financial Parameters",
            Row("Parameter (Rs. Crore)", "31 Mar, 2015", "31 Mar, 2016", "31 Mar, 2017"),
            Row("Employee benefits expense", 100.94, 93.02, 96.22),
            Row("Proposed dividend", "No", "No", "No"),
            Row("Prescribed CSR expenditure", "-", "-", "-"));

        var r = FinancialParametersParser.Parse(null, annexure, 1, 1, 10);

        var ebe2017 = Assert.Single(r.Items, x => x.ParameterName == "Employee benefits expense" && x.FinancialYear == 2017);
        Assert.Equal(96.22m, ebe2017.NumericValue);
        Assert.Equal("Rs. Crore", ebe2017.Unit);

        var div = Assert.Single(r.Items, x => x.ParameterName == "Proposed dividend" && x.FinancialYear == 2016);
        Assert.Null(div.NumericValue);
        Assert.Equal("No", div.TextValue);

        Assert.DoesNotContain(r.Items, x => x.ParameterName == "Prescribed CSR expenditure"); // all "-" -> nothing
    }
}

public class SecuritiesAllotmentParserTests
{
    [Fact]
    public void ParsesRows()
    {
        var sheet = Sheet("Securities Allotment",
            Row("ALLOTMENT DATE", "ALLOTMENT TYPE", "INSTRUMENT", "AMOUNT (Rs. Crore)", "NO. OF SECURITIES ALLOTTED", "NOMINAL VALUE", "PREMIUM VALUE"),
            Row("23 Dec, 2015", "Cash", "Equity Shares", 168.36, 168358227.0, 10.0, 0.0),
            Row("28 Oct, 2015", "Cash", "Equity Shares", 1.85, 17866.0, 10.0, 1025.0));

        var r = SecuritiesAllotmentParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal(new DateOnly(2015, 12, 23), r.Items[0].AllotmentDate);
        Assert.Equal(168.36m, r.Items[0].AmountCrore);
        Assert.Equal(1025.0m, r.Items[1].PremiumValuePerShare);
    }
}

public class ProprietorshipParserTests
{
    [Fact]
    public void SplitsDinFromName()
    {
        var sheet = Sheet("Proprietorship",
            Row("DIRECTOR NAME", "LEGAL NAME", "BUSINESS NAMES", "PAN", "STATUS"),
            Row("SURENDRA BABU SABBINENI (DIN : 05187341)", "SURENDRA BABU SABBINENI", "SABBINENI SURENDRA", "*****3461E", "Active"));

        var r = ProprietorshipParser.Parse(sheet, 1, 1, 10);

        var p = Assert.Single(r.Items);
        Assert.Equal("05187341", p.DirectorDin);
        Assert.Equal("Active", p.Status);
        Assert.Equal("SABBINENI SURENDRA", p.BusinessNames);
    }
}

public class DirectorAssociationHistoryParserTests
{
    [Fact]
    public void ParsesDesignationStints()
    {
        var sheet = Sheet("Director - Association History",
            Row("DIRECTOR NAME", "DESIGNATION", "APPOINTMENT DATE", "CESSATION DATE"),
            Row("SURENDRA BABU SABBINENI (DIN : 05187341)", "Managing Director", "1 Apr, 2010", "1 Apr, 2013"),
            Row("SURENDRA BABU SABBINENI (DIN : 05187341)", "Director", "1 May, 1995", "2 Jan, 2000"));

        var r = DirectorAssociationHistoryParser.Parse(sheet, 1, 1, 10);

        Assert.Equal(2, r.Items.Count);
        Assert.Equal("05187341", r.Items[0].DirectorDin);
        Assert.Equal("Managing Director", r.Items[0].Designation);
        Assert.Equal(new DateOnly(2013, 4, 1), r.Items[0].CessationDate);
    }
}

public class PeerComparisonParserTests
{
    [Fact]
    public void EmitsMetricPerYearWithMathematicalPositionOnly()
    {
        var sheet = Sheet("Peer Comparison",
            Row("COMPARATIVE METRICS 1"),
            Row("Industry", "Infrastructure"),
            Row("Segment", "Other Construction Services"),
            Row("Financial Year", 2017.0),
            Row(),
            Row("Metrics", "FY 2016", "", "FY 2017", ""),
            Row("", "Actual Value", "Median", "Actual Value", "Median"),
            Row("# of Peers in Sample", 29.0, "", 30.0, ""),
            Row("EBITDA Margin (%)", 19.4, 8.9, 40.8, 8.9),
            Row("Debtors / Sales (Days)", 434.0, 77.0, 480.0, 95.0),
            Row("5 Closest Peers by Revenue"));

        var r = PeerComparisonParser.Parse(sheet, 1, 1, 10);

        var ebitda2017 = Assert.Single(r.Items, x => x.MetricName == "EBITDA Margin (%)" && x.FinancialYear == 2017);
        Assert.Equal(40.8m, ebitda2017.CompanyValue);
        Assert.Equal(8.9m, ebitda2017.PeerMedianValue);
        Assert.Equal(30, ebitda2017.PeerCount);
        Assert.Equal(PeerPosition.Above, ebitda2017.Position);
        Assert.Equal("Infrastructure", ebitda2017.Industry);

        // "Above" is favourable for a margin, adverse for debtor days — position stays purely mathematical.
        var debtors2017 = Assert.Single(r.Items, x => x.MetricName == "Debtors / Sales (Days)" && x.FinancialYear == 2017);
        Assert.Equal(PeerPosition.Above, debtors2017.Position);
        Assert.DoesNotContain(r.Items, x => x.MetricName.StartsWith("5 Closest"));
    }
}
