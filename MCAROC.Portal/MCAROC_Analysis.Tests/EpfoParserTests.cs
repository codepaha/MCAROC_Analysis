using MCAROC_Analysis.Services.Excel.Parsers;
using Xunit;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>A5 / #36 — the annexure parser keeps the per-month TRRN, and the summary sheet is parsed
/// into <c>EpfoEstablishment</c> for the header metadata (city, setup date, principal activity,
/// address, exemption status) the monthly rows drop.</summary>
public class EpfoParserTests
{
    [Fact]
    public void Annexure_parse_keeps_the_monthly_TRRN()
    {
        var sheet = Sheet("Annexure - EPFO Establishments",
            Row("WORKING STATUS", "ESTABLISHMENT ID", "ESTABLISHMENT NAME", "WAGE MONTH", "TRRN", "NO. OF EMPLOYEES", "AMOUNT (Rs. Crore)", "DATE OF CREDIT", "PAYMENT DUE DATE", "STATUS"),
            Row("LIVE ESTABLISHMENT", "ORBBS0006003000", "COASTAL PROJECTS LTD.", "May, 2026", 2606360162374L, 5.0, 0.0, "9 Jun, 2026", "15 Jun, 2026", "Paid on Time"),
            Row("LIVE ESTABLISHMENT", "ORBBS0006003000", "COASTAL PROJECTS LTD.", "Apr, 2026", "2605360144200", 5.0, 0.12, "14 May, 2026", "15 May, 2026", "Paid on Time"));

        var result = EpfoParser.Parse(sheet, 1, 1, 1);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal("2606360162374", result.Items[0].Trrn); // numeric cell → plain digits, no ".0" / exponent
        Assert.Equal("2605360144200", result.Items[1].Trrn);
        Assert.Equal("May, 2026", result.Items[0].WageMonth);
        Assert.Equal(5, result.Items[0].EmployeeCount);
    }

    [Fact]
    public void Summary_sheet_parses_the_per_establishment_header_metadata()
    {
        // Header names only the first 8 columns; the trailing metadata columns are unlabelled — the
        // parser reads them positionally, exactly as the benchmark export lays them out.
        var sheet = Sheet("EPFO Establishments",
            Row("WORKING STATUS", "ESTABLISHMENT ID", "ESTABLISHMENT NAME", "CITY", "LATEST WAGE MONTH", "LATEST DATE OF CREDIT", "NO. OF EMPLOYEES", "AMOUNT"),
            Row("LIVE ESTABLISHMENT", "ORBBS0006003000", "COASTAL PROJECTS  LTD.", "BHUBANESWAR", "May, 2026", "9 Jun, 2026", 5.0, 0.0,
                "5 May, 1995", "BUILDING AND CONSTRUCTION INDUSTRY", "237, BAPUJI NAGAR, BHUBANESWAR, ODISHA, 751009,", "PF: UNEXEMPTED"),
            Row("CLOSED/ALL MEMBER EXCLUDED", "ORKJR0007806000", "COASTAL PROJECT (P) LTD.", "JODA", "-", "-", "-", "-",
                "-", "ENGINEERS - ENGG. CONTRACTORS", "C/O-K.D. SHARMA, JODA, ODISHA, 758038,", "PF: UNEXEMPTED"));

        var result = EpfoParser.ParseEstablishments(sheet, 1, 1, 1);

        Assert.Equal(2, result.Items.Count);

        var live = result.Items.Single(x => x.EstablishmentId == "ORBBS0006003000");
        Assert.Equal("COASTAL PROJECTS  LTD.", live.Name);
        Assert.Equal("BHUBANESWAR", live.City);
        Assert.Equal(new DateOnly(1995, 5, 5), live.DateOfSetup);
        Assert.Equal("BUILDING AND CONSTRUCTION INDUSTRY", live.PrincipalBusinessActivities);
        Assert.Equal("PF: UNEXEMPTED", live.ExemptionStatus);
        Assert.Contains("BAPUJI NAGAR", live.Address);
        Assert.Equal("LIVE ESTABLISHMENT", live.WorkingStatus);

        var closed = result.Items.Single(x => x.EstablishmentId == "ORKJR0007806000");
        Assert.Null(closed.DateOfSetup);          // "-" → null, not a parse failure
        Assert.Null(closed.LatestWageMonth);
        Assert.Equal("ENGINEERS - ENGG. CONTRACTORS", closed.PrincipalBusinessActivities);
    }

    [Fact]
    public void Summary_sheet_keeps_the_first_row_on_a_duplicate_establishment_id_and_warns()
    {
        var sheet = Sheet("EPFO Establishments",
            Row("WORKING STATUS", "ESTABLISHMENT ID", "ESTABLISHMENT NAME", "CITY"),
            Row("LIVE", "ORBBS0006003000", "FIRST NAME", "BHUBANESWAR"),
            Row("LIVE", "ORBBS0006003000", "SECOND NAME", "CUTTACK"));

        var result = EpfoParser.ParseEstablishments(sheet, 1, 1, 1);

        Assert.Equal("FIRST NAME", Assert.Single(result.Items).Name);
        Assert.Contains(result.Warnings, w => w.IssueCode == "EPFO_ESTABLISHMENT_DUPLICATE");
    }
}
