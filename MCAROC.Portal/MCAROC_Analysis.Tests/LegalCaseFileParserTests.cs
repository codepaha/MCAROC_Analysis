using System.Text;
using MCAROC_Analysis.Services.PreLoginReports;

namespace MCAROC_Analysis.Tests;

public class LegalCaseFileParserTests
{
    static LegalCaseFileParserTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private const string Header = "Court,Case_Number,Role,Case_Type,Case_Status,Case_Stage,Filing_Date,Case_Year,Court_Forum,State,District,Petitioners,Petitioner_Advocates,Respondents,Respondent_Advocates,LDOH_NDOH,Act,Cnr_Number,Bench,Side";

    private static Stream Csv(params string[] rows) => new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\r\n", [Header, .. rows])));

    [Fact]
    public void Parses_court_forum_as_the_court_name_not_the_category_column()
    {
        var records = LegalCaseFileParser.Parse(
            Csv("district,101/2020,AGAINST,CS,PENDING,NA,01-01-2020,2020,\"Civil Judge, Rohtak\",Haryana,Rohtak,A Ltd,,B Ltd,,15-09-2026,CPC,CNR1,,other"),
            "cases.csv");

        Assert.Single(records);
        Assert.Equal("district", records[0].Category);
        Assert.Equal("Civil Judge, Rohtak", records[0].Court);
        Assert.Equal("A Ltd VS B Ltd", records[0].CaseDetails);
        Assert.Equal("CNR1", records[0].CnrNumber);
        Assert.Equal("01-01-2020", records[0].DateOfFiling);
    }

    [Fact]
    public void Falls_back_to_bench_when_court_forum_is_blank()
    {
        var records = LegalCaseFileParser.Parse(
            Csv("NA,73/2020,,interlocutory application,NA,NA,NA,2020,NA,NA,NA,A Ltd,,B Ltd,,NA,NA,CNR1,\"nclt,cuttack bench.\",other"),
            "cases.csv");

        Assert.Single(records);
        Assert.Equal("Nclt, Cuttack Bench", records[0].Court);
    }

    [Fact]
    public void Dedupes_parties_listed_twice_with_a_serial_prefix_and_strips_embedded_advocate_annotations()
    {
        var respondents = "RBI AND 19 OTHERS, , STATE BANK OF INDIA, , 1) RBI AND 19 OTHERS, , 2) STATE BANK OF INDIA, , " +
                           "STATE BANK OF INDIA ADVOCATE -PEARL LAW ASSOCIATES";
        var records = LegalCaseFileParser.Parse(
            Csv($"highcourt,1/2021,,WP,PENDING,ADMISSION,01-01-2021,2021,High Court,Telangana,Hyderabad,Petitioner Co,,\"{respondents}\",,NA,Constitution,CNR2,,other"),
            "cases.csv");

        Assert.Single(records);
        Assert.Equal("Petitioner Co VS RBI AND 19 OTHERS, STATE BANK OF INDIA", records[0].CaseDetails);
    }

    [Fact]
    public void Maps_case_status_and_case_stage_directly_by_name()
    {
        var records = LegalCaseFileParser.Parse(
            Csv("district,1/2020,,CS,PENDING,Plantiff's Evidence,01-01-2020,2020,Civil Court,Haryana,Rohtak,A,,B,,15-09-2026,CPC,CNR,,other"),
            "cases.csv");

        Assert.Equal("PENDING", records[0].Status);
        Assert.Equal("Plantiff's Evidence", records[0].CaseStage);
        Assert.Equal("15-09-2026", records[0].DateOfHearing);
    }

    [Fact]
    public void Skips_blank_spacer_rows()
    {
        var records = LegalCaseFileParser.Parse(
            Csv("district,1/2020,,CS,PENDING,NA,01-01-2020,2020,Civil Court,Haryana,Rohtak,A,,B,,NA,CPC,CNR,,other", ",,,,,,,,,,,,,,,,,,"),
            "cases.csv");

        Assert.Single(records);
    }

    [Fact]
    public void Throws_when_no_recognizable_cases_are_found()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("Name,Age\r\nA,1\r\n"));
        var ex = Assert.Throws<PreLoginReportException>(() => LegalCaseFileParser.Parse(stream, "not-cases.csv"));
        Assert.Contains("No litigation cases", ex.Message);
    }

    [Theory]
    [InlineData("supreme", nameof(InstaLegalCases.SupremeCourt))]
    [InlineData("highcourt", nameof(InstaLegalCases.HighCourt))]
    [InlineData("district", nameof(InstaLegalCases.DistrictCourt))]
    [InlineData("consumer", nameof(InstaLegalCases.ConsumerForum))]
    [InlineData("itat", nameof(InstaLegalCases.ItatTax))]
    [InlineData("cestat", nameof(InstaLegalCases.ItatTax))]
    [InlineData("drt", nameof(InstaLegalCases.DrtDrat))]
    [InlineData("nclt", nameof(InstaLegalCases.NcltNclat))]
    [InlineData("nclat", nameof(InstaLegalCases.NcltNclat))]
    [InlineData("rera", nameof(InstaLegalCases.Rera))]
    [InlineData("appellate", nameof(InstaLegalCases.NgtOthers))]
    public void ToInstaLegalCases_buckets_each_category_into_its_summary_column(string category, string expectedProperty)
    {
        var cases = new[] { new LegalCaseRecord(category, "Court", "-", "1/2020", "CS", "2020", "-", "-", "-", "-", "-", "A VS B", "-", "PENDING") };

        var counts = LegalCaseFileParser.ToInstaLegalCases(cases);

        var actual = typeof(InstaLegalCases).GetProperty(expectedProperty)!.GetValue(counts);
        Assert.Equal(1, actual);
        Assert.Same(cases, counts.Cases);
    }
}
