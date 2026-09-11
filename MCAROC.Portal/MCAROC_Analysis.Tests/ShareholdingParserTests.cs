using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class ShareholdingParserTests
{
    [Fact]
    public void MergesSamePersonAppearingInBothSheetsIntoOneRecord()
    {
        var directorSheet = Sheet("Director Shareholding",
            Row("DIRECTORS SHAREHOLDING - 31 Mar, 2025", "", "", "", ""),
            Row("Name", "Designation", "Shareholding (%)", "Number of Shares", "Cessation Date"),
            Row("AMITABH SARAN", "Director", 20.67, 58538.0, "-"));

        var majorSheet = Sheet("Shareholding More Than 5%",
            Row("SHAREHOLDING MORE THAN 5%", "", "", "", "", "", "", "", "", "", "", ""),
            Row("Financial Year Ending On", "Entity Name", "Relationship", "Entity Type", "Shareholding (%)", "Country / City",
                "Paid Up Capital", "Sum Of Charges", "Date Of Incorporation", "Company / LLP Status", "Active Compliance", "Remarks"),
            Row("31 Mar, 2025", "AMITABH SARAN", "SHAREHOLDER", "INDIVIDUALS", 20.67, "-", "-", "-", "-", "-", "-", "Person holding DIN"));

        var result = ShareholdingParser.Parse(directorSheet, majorSheet, requestId: 1, ingestionRunId: 1, 10, 10);

        var record = Assert.Single(result.Items);
        Assert.Equal(58538, record.SharesHeld); // came from the Director Shareholding sheet
        Assert.Equal(20.67m, record.HoldingPercentage);
        Assert.Equal(ShareholdingSourceType.MajorShareholding, record.SourceType); // richer sheet wins the tag
        Assert.Equal(2025, record.FinancialYear);
        Assert.Equal("Director", record.Designation); // from the Director Shareholding sheet
        Assert.Null(record.CessationDate); // "-" means still holding
        Assert.Equal("SHAREHOLDER", record.RelationshipRaw); // from the >5% sheet
        Assert.Equal("Person holding DIN", record.Remarks);
        Assert.Null(record.Location); // "-" in the sample
    }

    [Fact]
    public void CapturesTheFullColumnSetOfTheMajorShareholdingSheet()
    {
        var majorSheet = Sheet("Shareholding More Than 5%",
            Row("SHAREHOLDING MORE THAN 5%"),
            Row("Financial Year Ending On", "Entity Name", "Relationship", "Entity Type", "Shareholding (%)", "Country / City",
                "Paid Up Capital", "Sum Of Charges", "Date Of Incorporation", "Company / LLP Status", "Active Compliance", "Remarks"),
            Row("31 Mar, 2017", "SABBINENI SURENDRA", "SHAREHOLDER", "INDIVIDUALS", 10.23, "Hyderabad",
                533.1, 1279.0, "25 Aug, 2004", "Active", "Active Compliant", "Person holding DIN"));

        var result = ShareholdingParser.Parse(null, majorSheet, 1, 1, null, 10);

        var record = Assert.Single(result.Items);
        Assert.Equal("SHAREHOLDER", record.RelationshipRaw);
        Assert.Equal("Hyderabad", record.Location);
        Assert.Equal(533.1m, record.PaidUpCapitalCrore);
        Assert.Equal(1279.0m, record.SumOfChargesCrore);
        Assert.Equal(new DateOnly(2004, 8, 25), record.DateOfIncorporation);
        Assert.Equal("Active", record.CompanyStatus);
        Assert.Equal("Active Compliant", record.ActiveCompliance);
        Assert.Equal("Person holding DIN", record.Remarks);
    }

    [Fact]
    public void CapturesDesignationAndCessationDateFromDirectorShareholding()
    {
        var directorSheet = Sheet("Director Shareholding",
            Row("DIRECTORS SHAREHOLDING - 31 Mar, 2017", "", "", "", ""),
            Row("Name", "Designation", "Shareholding (%)", "Number of Shares", "Cessation Date"),
            Row("A FORMER DIRECTOR", "Managing Director", 5.0, 1000.0, "15 Jan, 2018"));

        var result = ShareholdingParser.Parse(directorSheet, null, 1, 1, 10, null);

        var record = Assert.Single(result.Items);
        Assert.Equal("Managing Director", record.Designation);
        Assert.Equal(new DateOnly(2018, 1, 15), record.CessationDate);
    }

    [Fact]
    public void KeepsDistinctPeopleAsSeparateRecords()
    {
        var majorSheet = Sheet("Shareholding More Than 5%",
            Row("SHAREHOLDING MORE THAN 5%"),
            Row("Financial Year Ending On", "Entity Name", "Relationship", "Entity Type", "Shareholding (%)"),
            Row("31 Mar, 2025", "PERSON A", "SHAREHOLDER", "INDIVIDUALS", 30.0),
            Row("31 Mar, 2025", "PERSON B", "SHAREHOLDER", "INDIVIDUALS", 15.0));

        var result = ShareholdingParser.Parse(null, majorSheet, 1, 1, null, 10);

        Assert.Equal(2, result.Items.Count);
    }
}
