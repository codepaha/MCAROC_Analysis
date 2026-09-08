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
