using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

public class DirectorsParserTests
{
    [Fact]
    public void ParsesCleanRowAndZeroPadsDin()
    {
        var sheet = Sheet("Directors",
            Row("NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"),
            Row("AMITABH SARAN", 415231.0, "Director", "8 Feb, 2013", "8 Feb, 2013", "-", "-"));

        var result = DirectorsParser.Parse(sheet, requestId: 1, ingestionRunId: 1, sourceDocumentId: 10);

        var director = Assert.Single(result.Items);
        Assert.Equal("00415231", director.Din);
        Assert.Equal("AMITABH SARAN", director.NameRaw);
        Assert.Equal(new DateOnly(2013, 2, 8), director.OriginalAppointmentDate);
        Assert.Null(director.CessationDate);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void WarnsAndSkipsRowWithUnparsableDin()
    {
        var sheet = Sheet("Directors",
            Row("NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"),
            Row("BAD ROW", "not-a-din", "Director", "-", "-", "-", "-"));

        var result = DirectorsParser.Parse(sheet, 1, 1, null);

        Assert.Empty(result.Items);
        Assert.Single(result.Warnings);
        Assert.Equal("BAD_DIN", result.Warnings[0].IssueCode);
    }
}
