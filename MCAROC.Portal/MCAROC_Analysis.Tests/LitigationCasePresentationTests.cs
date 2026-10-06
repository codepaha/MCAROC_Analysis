using System.Text;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationCasePresentationTests
{
    [Theory]
    [InlineData("supreme_court", "SupremeCourt", LitigationInstitution.SupremeCourt)]
    [InlineData("high_court", "BombayHighCourt", LitigationInstitution.HighCourt)]
    [InlineData("district_court", "ADDL. CHIEF JUDICIAL MAGISTRATE, ANDHERI", LitigationInstitution.DistrictCourt)]
    // The vendor files these under a "high risk court" category that is not a forum: the court name decides.
    [InlineData("high_risk_court", "Drt", LitigationInstitution.Drt)]
    [InlineData("high_risk_court", "Nclt", LitigationInstitution.Nclt)]
    [InlineData("high_risk_court", "nclt", LitigationInstitution.Nclt)]
    [InlineData("high_risk_court", "Nclat", LitigationInstitution.Nclat)]
    [InlineData("tribunal_cases", "Drat", LitigationInstitution.Drat)]
    [InlineData("tribunal_cases", "Itat", LitigationInstitution.Itat)]
    [InlineData("tribunal_cases", "Cestat", LitigationInstitution.Cestat)]
    [InlineData("tribunal_cases", "Consumer Court", LitigationInstitution.ConsumerCourt)]
    [InlineData("tribunal_cases", "Unspecified Court", LitigationInstitution.Others)]
    [InlineData("tribunal_cases", null, LitigationInstitution.Others)]
    [InlineData(null, "Debts Recovery Tribunal, Kolkata", LitigationInstitution.Drt)]
    public void Resolve_maps_vendor_category_and_court_name_to_a_fixed_forum(string? category, string? court, LitigationInstitution expected) =>
        Assert.Equal(expected, LitigationCasePresentation.Resolve(category, court));

    [Fact]
    public void Resolve_does_not_mistake_an_appellate_tribunal_for_its_first_instance_forum()
    {
        Assert.Equal(LitigationInstitution.Nclat, LitigationCasePresentation.Resolve(null, "NCLAT Principal Bench"));
        Assert.Equal(LitigationInstitution.Drat, LitigationCasePresentation.Resolve(null, "DRAT Mumbai"));
    }

    private static readonly string[] Targets = ["Coastal Projects Limited", "COASTAL PROJECTS PRIVATE LIMITED", "Coastal Projects Ltd"];

    [Theory]
    [InlineData("by", "[]", "[]", LitigationDirection.By)]
    [InlineData("against", "[]", "[]", LitigationDirection.Against)]
    [InlineData("AGAINST", """[{"name":"COASTAL PROJECTS LTD"}]""", "[]", LitigationDirection.Against)] // the vendor wins, even over the party lists
    public void ResolveDirection_prefers_the_vendors_direction(string vendor, string pet, string res, LitigationDirection expected) =>
        Assert.Equal(expected, LitigationCasePresentation.ResolveDirection(vendor, pet, res, Targets));

    [Theory]
    // company among the petitioners: it brought the case
    [InlineData("""[{"name":"COASTAL PROJECTS PRIVATE LIMITED","address":"Hyderabad"}]""", """[{"name":"DY. COMMISSIONER OF INCOME TAX"}]""", LitigationDirection.By)]
    [InlineData("""[{"name":"M/S COASTAL PROJECTS (P) LTD"}]""", """[{"name":"ACIT"}]""", LitigationDirection.By)]
    // company among the respondents: it is defending
    [InlineData("""[{"name":"OM PRAKASH"}]""", """[{"name":"COASTAL PROJECTS LTD."}]""", LitigationDirection.Against)]
    [InlineData("""[{"name":"IFCI LTD"}]""", """[{"name":"COASTAL PROJECTS LTD  and ORS."}]""", LitigationDirection.Against)]
    // on both sides, or on neither: not guessed
    [InlineData("""[{"name":"COASTAL PROJECTS LTD"}]""", """[{"name":"COASTAL PROJECTS PVT LTD"}]""", LitigationDirection.Unspecified)]
    [InlineData("""[{"name":"A"}]""", """[{"name":"B"}]""", LitigationDirection.Unspecified)]
    [InlineData(null, null, LitigationDirection.Unspecified)]
    // a different company that merely shares a word is not the target
    [InlineData("""[{"name":"COASTAL PROJECT"}]""", """[{"name":"X"}]""", LitigationDirection.Unspecified)]
    public void ResolveDirection_reads_the_side_from_the_party_lists_when_the_vendor_gives_none(string? pet, string? res, LitigationDirection expected) =>
        Assert.Equal(expected, LitigationCasePresentation.ResolveDirection(null, pet, res, Targets));

    [Fact]
    public void ResolveDirection_without_a_target_name_is_unspecified() =>
        Assert.Equal(LitigationDirection.Unspecified,
            LitigationCasePresentation.ResolveDirection(null, """[{"name":"COASTAL PROJECTS LTD"}]""", "[]", []));

    private static readonly DateOnly Today = new(2026, 10, 6);

    [Theory]
    [InlineData("21-12-2023", null, LitigationCaseStatusBucket.Pending, "Pending 2 yrs 9 mths")]
    [InlineData("06-10-2025", null, LitigationCaseStatusBucket.Pending, "Pending 1 yr")]
    [InlineData("07-10-2025", null, LitigationCaseStatusBucket.Pending, "Pending 11 mths")] // one day short of a year
    [InlineData("26-09-2026", null, LitigationCaseStatusBucket.Pending, "Pending 10 days")]
    [InlineData("2024-03-01", null, LitigationCaseStatusBucket.Pending, "Pending 2 yrs 7 mths")] // ISO dates too
    [InlineData("10-03-2016", "09-12-2023", LitigationCaseStatusBucket.Disposed, "Disposed in 7 yrs 8 mths")]
    [InlineData("04-08-2015", "12-05-2016", LitigationCaseStatusBucket.Disposed, "Disposed in 9 mths")]
    [InlineData("28-01-2016", null, LitigationCaseStatusBucket.Unknown, "Filed 10 yrs 8 mths ago")]
    // missing or contradictory dates never produce a made-up age
    [InlineData(null, null, LitigationCaseStatusBucket.Pending, "-")]
    [InlineData("not a date", null, LitigationCaseStatusBucket.Pending, "-")]
    [InlineData("10-03-2016", null, LitigationCaseStatusBucket.Disposed, "-")]
    [InlineData("10-03-2016", "01-01-2015", LitigationCaseStatusBucket.Disposed, "-")]
    [InlineData("01-01-2027", null, LitigationCaseStatusBucket.Pending, "-")]
    public void Age_describes_how_long_a_case_has_been_open_or_took_to_decide(string? filed, string? decided, LitigationCaseStatusBucket bucket, string expected) =>
        Assert.Equal(expected, LitigationCasePresentation.Age(filed, decided, bucket, Today));

    // ── report order ─────────────────────────────────────────────────────────────────────────────────────

    private static StandaloneReportCaseDto Case(long id, string category, string court, string status, string? direction, string? filed, string? petitioner = null) =>
        new(id, null, "csp-" + id, "CNR" + id, category, direction, null, null, court, null, "CASE/" + id, null, null, null, status, null,
            filed, null, null, status.StartsWith("Disposed", StringComparison.OrdinalIgnoreCase) ? "01-01-2024" : null, null, null,
            petitioner, null, null, null, []);

    private static StandaloneLitigationReport Report(params StandaloneReportCaseDto[] cases) => new(
        "MCA-1", "Coastal Projects Limited", new DateTimeOffset(2026, 10, 6, 4, 0, 0, TimeSpan.Zero), 5, new DateTime(2026, 10, 5, 5, 41, 0, DateTimeKind.Utc),
        false, ["Coastal Projects Limited"], new LitigationCourtSummaryGrid(), cases);

    [Fact]
    public void Cases_are_listed_pending_then_disposed_then_unknown_each_by_forum_seniority_and_against_before_by()
    {
        var report = Report(
            Case(1, "tribunal_cases", "Itat", "Pending", "against", "01-01-2020"),
            Case(2, "district_court", "Sub Judge", "Pending", "against", "01-01-2020"),
            Case(3, "supreme_court", "SupremeCourt", "Pending", "by", "01-01-2020"),
            Case(4, "high_court", "DelhiHighCourt", "Pending", "by", "01-01-2021"),
            Case(5, "high_court", "DelhiHighCourt", "Pending", "against", "01-01-2019"),
            Case(6, "high_court", "DelhiHighCourt", "Pending", "against", "01-01-2022"), // newer: before case 5
            Case(7, "high_court", "DelhiHighCourt", "Disposed", "against", "01-01-2018"),
            Case(8, "high_court", "DelhiHighCourt", "Fixed", "against", "01-01-2018"),
            Case(9, "high_court", "DelhiHighCourt", "Pending", "against", null)); // no filing date: last of its group

        var order = LitigationReportArtifacts.BuildCaseViews(report).Select(v => v.Item.LitigationCaseId).ToArray();

        // pending: SC, then HC (against: newest, older, undated; then by), then DC, then tribunal; then disposed; then unknown
        Assert.Equal([3, 6, 5, 9, 4, 2, 1, 7, 8], order);
    }

    [Fact]
    public void Case_views_resolve_a_missing_direction_from_the_party_lists_and_carry_the_age()
    {
        var report = Report(Case(1, "tribunal_cases", "Itat", "Pending", null, "06-10-2025",
            petitioner: """[{"name":"COASTAL PROJECTS PRIVATE LIMITED"}]"""));

        var view = Assert.Single(LitigationReportArtifacts.BuildCaseViews(report));

        Assert.Equal(LitigationDirection.By, view.Direction);
        Assert.Equal(LitigationInstitution.Itat, view.Institution);
        Assert.Equal("Pending 1 yr", view.Age);
    }

    [Fact]
    public void Csv_follows_report_order_and_carries_forum_and_age_but_never_the_internal_id()
    {
        var report = Report(
            Case(1, "district_court", "Sub Judge", "Pending", "against", "06-10-2025"),
            Case(2, "supreme_court", "SupremeCourt", "Pending", "by", "06-10-2024"));

        using var reader = new StringReader(Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report)));
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csv.Read(); csv.ReadHeader();
        Assert.DoesNotContain("CSP ID", csv.HeaderRecord!);

        csv.Read();
        Assert.Equal("Supreme Court", csv.GetField("Forum"));
        Assert.Equal("Pending 2 yrs", csv.GetField("Case Age"));
        Assert.Equal("By", csv.GetField("Direction"));
        csv.Read();
        Assert.Equal("District Court", csv.GetField("Forum"));
        Assert.Equal("Against", csv.GetField("Direction"));
    }

    // ── PDF ──────────────────────────────────────────────────────────────────────────────────────────────

    private static string PdfText(StandaloneLitigationReport report, byte[]? logo = null)
    {
        using var doc = PdfDocument.Open(new MemoryStream(LitigationReportArtifacts.RenderPdf(report, logo)));
        return string.Concat(doc.GetPages().Select(p => p.Text));
    }

    [SkippableFact]
    public void Pdf_shows_pending_and_disposed_sections_a_forum_summary_and_age_but_no_internal_id_or_retrieval_status()
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var report = Report(
            Case(1, "high_court", "DelhiHighCourt", "Pending", "against", "06-10-2025"),
            Case(2, "high_risk_court", "Drt", "Disposed", "against", "10-03-2016"),
            Case(3, "tribunal_cases", "Itat", "Fixed", null, "06-10-2025"));
        var text = PdfText(report);

        Assert.Contains("PENDING CASES", text);
        Assert.Contains("DISPOSED CASES", text);
        Assert.Contains("NOT REPORTED", text);
        Assert.Contains("FORUM WISE", text);
        Assert.Contains("CASE AGE", text);
        Assert.Contains("Pending 1 yr", text);
        Assert.Contains("Against the company", text);
        Assert.DoesNotContain("csp-1", text);
        Assert.DoesNotContain("CSP", text);
        Assert.DoesNotContain("Retrieval failed", text);
        Assert.DoesNotContain("Retrieval pending", text);
    }

    [Fact]
    public void Pdf_renders_with_and_without_the_logo_and_with_no_cases()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var withCases = Report(Case(1, "high_court", "DelhiHighCourt", "Pending", "against", "06-10-2025"));

        Assert.True(LitigationReportArtifacts.RenderPdf(withCases, png).Length > 1000);
        Assert.True(LitigationReportArtifacts.RenderPdf(withCases, null).Length > 1000);
        Assert.True(LitigationReportArtifacts.RenderPdf(Report(), png).Length > 1000);
    }
}
