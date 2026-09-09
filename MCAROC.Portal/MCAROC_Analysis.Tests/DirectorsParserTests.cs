using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class DirectorsParserTests
{
    private static readonly string[] Header =
        ["NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"];

    [Fact]
    public void ParsesCleanRowAndZeroPadsDin()
    {
        var sheet = Sheet("Directors",
            Row(Header),
            Row("AMITABH SARAN", 415231.0, "Director", "8 Feb, 2013", "8 Feb, 2013", "-", "-"));

        var result = DirectorsParser.Parse(sheet, 1, 1, 10, out var officers);

        var director = Assert.Single(result.Items);
        Assert.Equal("00415231", director.Din);
        Assert.Equal("AMITABH SARAN", director.NameRaw);
        Assert.Equal(new DateOnly(2013, 2, 8), director.OriginalAppointmentDate);
        Assert.Null(director.CessationDate);
        Assert.Empty(result.Errors);
        Assert.Empty(officers);
    }

    [Fact]
    public void WarnsAndSkipsRowWithUnparsableDin()
    {
        var sheet = Sheet("Directors",
            Row(Header),
            Row("BAD ROW", "not-a-din", "Director", "-", "-", "-", "-"));

        var result = DirectorsParser.Parse(sheet, 1, 1, null, out var officers);

        Assert.Empty(result.Items);
        Assert.Empty(officers);
        Assert.Equal("BAD_DIN", Assert.Single(result.Warnings).IssueCode);
    }

    [Fact]
    public void CapturesNonDinOfficerRowsAsCompanyOfficers()
    {
        var sheet = Sheet("Directors",
            Row(Header),
            Row("RAMESH VENKATARAMAN", 8234561.0, "Managing Director", "2 Apr, 2015", "2 Apr, 2015", "-", "-"),
            Row("CHINMOY PATNAIK", "-", "Company Secretary", "1 Apr, 2020", "1 Apr, 2020", "-", "-"),
            Row("NARALA VARA LAKSHMI", "NA", "Manager", "-", "10 May, 2018", "31 Mar, 2022", "-"));

        var result = DirectorsParser.Parse(sheet, 5, 3, 10, out var officers);

        var director = Assert.Single(result.Items);
        Assert.Equal("08234561", director.Din);

        Assert.Equal(2, officers.Count);
        var cs = officers.Single(o => o.NameRaw == "CHINMOY PATNAIK");
        Assert.Equal("Company Secretary", cs.Designation);
        Assert.Equal("-", cs.DinCellRaw);
        Assert.Equal(new DateOnly(2020, 4, 1), cs.OriginalAppointmentDate);
        Assert.Equal(5, cs.RequestId);
        Assert.Equal(3, cs.IngestionRunId);

        var mgr = officers.Single(o => o.NameRaw == "NARALA VARA LAKSHMI");
        Assert.Equal(new DateOnly(2022, 3, 31), mgr.CessationDate);

        Assert.Equal(2, result.Warnings.Count(w => w.IssueCode == "OFFICER_NO_DIN"));
    }
}
