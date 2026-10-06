using System.Text;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Tests;

public sealed class LitigationCasePresentationTests
{
    [Theory]
    // the vendor's own type decides, exactly as the baseline risk reads it
    [InlineData("supreme", "SupremeCourt", "supreme_court", LitigationInstitution.SupremeCourt)]
    [InlineData("highcourt", "BombayHighCourt", "high_court", LitigationInstitution.HighCourt)]
    [InlineData("district", "ADDL. CHIEF JUDICIAL MAGISTRATE, ANDHERI", "district_court", LitigationInstitution.DistrictCourt)]
    [InlineData("drt", "Drt", "high_risk_court", LitigationInstitution.Drt)]
    [InlineData("nclt", "Nclt", "high_risk_court", LitigationInstitution.Nclt)]
    [InlineData("nclat", "Nclat", "high_risk_court", LitigationInstitution.Nclat)]
    [InlineData("drat", "Drat", "tribunal_cases", LitigationInstitution.Drat)]
    [InlineData("itat", "Itat", "tribunal_cases", LitigationInstitution.Itat)]
    [InlineData("cestat", "Cestat", "tribunal_cases", LitigationInstitution.Cestat)]
    [InlineData("consumer", "Consumer Court", "tribunal_cases", LitigationInstitution.ConsumerCourt)]
    [InlineData("others", "Unspecified Court", "tribunal_cases", LitigationInstitution.Others)]
    [InlineData("rera", "State RERA", null, LitigationInstitution.Others)]
    // RERA is a known forum outside the list: it is Others even when the vendor files it under a court tier
    [InlineData("rera", "State RERA", "high_court", LitigationInstitution.Others)]
    [InlineData("rera", "State RERA", "district_court", LitigationInstitution.Others)]
    [InlineData("rera", "State RERA", "supreme_court", LitigationInstitution.Others)]
    [InlineData(null, "Real Estate Regulatory Authority", "district_court", LitigationInstitution.Others)]
    // no type: the court name, then the vendor's category for the three court tiers
    [InlineData(null, "Debts Recovery Tribunal, Kolkata", null, LitigationInstitution.Drt)]
    [InlineData(null, "SupremeCourt", "supreme_court", LitigationInstitution.SupremeCourt)]
    [InlineData(null, "KarnatakaHighCourt", "high_court", LitigationInstitution.HighCourt)]
    [InlineData(null, "Sub Judge", "district_court", LitigationInstitution.DistrictCourt)]
    // "high_risk_court" is not a forum
    [InlineData(null, null, "high_risk_court", LitigationInstitution.Others)]
    public void Resolve_maps_a_case_to_a_fixed_forum(string? type, string? court, string? category, LitigationInstitution expected) =>
        Assert.Equal(expected, LitigationCasePresentation.Resolve(type, court, category));

    [Fact]
    public void Resolve_does_not_mistake_an_appellate_tribunal_for_its_first_instance_forum()
    {
        Assert.Equal(LitigationInstitution.Nclat, LitigationCasePresentation.Resolve(null, "NCLAT Principal Bench", null));
        Assert.Equal(LitigationInstitution.Drat, LitigationCasePresentation.Resolve(null, "DRAT Mumbai", null));
    }

    private static readonly string[] Targets = ["Coastal Projects Limited", "COASTAL PROJECTS PRIVATE LIMITED", "Coastal Projects Ltd"];

    [Theory]
    // the vendor's direction settles the side when the party names do not
    [InlineData("by", "[]", "[]", LitigationCompanySide.ByCompany)]
    [InlineData("against", "[]", "[]", LitigationCompanySide.AgainstCompany)]
    [InlineData("AGAINST", """[{"name":"A"}]""", """[{"name":"B"}]""", LitigationCompanySide.AgainstCompany)]
    // a case naming the company on both sides keeps the vendor's direction rather than being left undetermined
    [InlineData("against", """[{"name":"COASTAL PROJECTS LTD"}]""", """[{"name":"COASTAL PROJECTS PVT LTD"}]""", LitigationCompanySide.AgainstCompany)]
    [InlineData("by", """[{"name":"COASTAL PROJECTS LTD"}]""", """[{"name":"COASTAL PROJECTS PVT LTD"}]""", LitigationCompanySide.ByCompany)]
    public void Side_uses_the_vendors_direction_when_the_names_leave_it_open(string vendor, string pet, string res, LitigationCompanySide expected) =>
        Assert.Equal(expected, LitigationCasePresentation.Side(vendor, pet, res, Targets));

    [Theory]
    // company among the petitioners: it brought the case
    [InlineData("""[{"name":"COASTAL PROJECTS PRIVATE LIMITED","address":"Hyderabad"}]""", """[{"name":"DY. COMMISSIONER OF INCOME TAX"}]""", LitigationCompanySide.ByCompany)]
    [InlineData("""[{"name":"M/S COASTAL PROJECTS (P) LTD"}]""", """[{"name":"ACIT"}]""", LitigationCompanySide.ByCompany)]
    // company among the respondents: it is defending
    [InlineData("""[{"name":"OM PRAKASH"}]""", """[{"name":"COASTAL PROJECTS LTD."}]""", LitigationCompanySide.AgainstCompany)]
    [InlineData("""[{"name":"IFCI LTD"}]""", """[{"name":"COASTAL PROJECTS LTD  and ORS."}]""", LitigationCompanySide.AgainstCompany)]
    // on both sides, or on neither, with no vendor direction: not guessed
    [InlineData("""[{"name":"COASTAL PROJECTS LTD"}]""", """[{"name":"COASTAL PROJECTS PVT LTD"}]""", LitigationCompanySide.Both)]
    [InlineData("""[{"name":"A"}]""", """[{"name":"B"}]""", LitigationCompanySide.Unknown)]
    [InlineData(null, null, LitigationCompanySide.Unknown)]
    // a different company that merely shares a word is not the target
    [InlineData("""[{"name":"COASTAL PROJECT"}]""", """[{"name":"X"}]""", LitigationCompanySide.Unknown)]
    public void Side_reads_the_company_from_the_party_lists_when_the_vendor_gives_none(string? pet, string? res, LitigationCompanySide expected) =>
        Assert.Equal(expected, LitigationCasePresentation.Side(null, pet, res, Targets));

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
        var report = Report(Case(1, "tribunal_cases", "Itat", "Pending", null, "05-10-2025",
            petitioner: """[{"name":"COASTAL PROJECTS PRIVATE LIMITED"}]"""));

        var view = Assert.Single(LitigationReportArtifacts.BuildCaseViews(report));

        Assert.Equal(LitigationCompanySide.ByCompany, view.Side);
        Assert.Equal(LitigationInstitution.Itat, view.Institution);
        Assert.Equal("Pending for 1 year", view.Age.Headline); // measured to the retrieval date, as on the case page
    }

    [Fact]
    public void Csv_follows_report_order_and_carries_forum_and_age_but_never_the_internal_id()
    {
        var report = Report(
            Case(1, "district_court", "Sub Judge", "Pending", "against", "05-10-2025"),
            Case(2, "supreme_court", "SupremeCourt", "Pending", "by", "05-10-2024"));

        using var reader = new StringReader(Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report)));
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csv.Read(); csv.ReadHeader();
        Assert.DoesNotContain("CSP ID", csv.HeaderRecord!);

        csv.Read();
        Assert.Equal("Supreme Court", csv.GetField("Forum"));
        Assert.Equal("Pending for 2 years", csv.GetField("Case Age"));
        Assert.Equal("By", csv.GetField("Direction"));
        csv.Read();
        Assert.Equal("District Court", csv.GetField("Forum"));
        Assert.Equal("Against", csv.GetField("Direction"));
    }

    [Fact]
    public void Csv_gives_every_case_an_explicit_direction_including_when_it_cannot_be_determined()
    {
        var both = Case(1, "high_court", "DelhiHighCourt", "Pending", null, "05-10-2025");
        both = both with { PetitionersJson = """[{"name":"COASTAL PROJECTS LTD"}]""", RespondentsJson = """[{"name":"COASTAL PROJECTS PVT LTD"}]""" };
        var neither = Case(2, "high_court", "DelhiHighCourt", "Pending", null, "05-10-2025");
        neither = neither with { PetitionersJson = """[{"name":"A"}]""", RespondentsJson = """[{"name":"B"}]""" };
        var noParties = Case(3, "high_court", "DelhiHighCourt", "Pending", null, "05-10-2025") with { PetitionersJson = null, RespondentsJson = null };
        var report = Report(both, neither, noParties);

        using var reader = new StringReader(Encoding.UTF8.GetString(LitigationReportArtifacts.RenderCsv(report)));
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csv.Read(); csv.ReadHeader();
        var directions = new List<string?>();
        while (csv.Read()) directions.Add(csv.GetField("Direction"));

        Assert.Equal(3, directions.Count);
        Assert.All(directions, d => Assert.Equal("Not determined", d)); // same wording as the report, never a blank cell
    }

    [Fact]
    public void Cases_that_tie_on_every_other_key_are_listed_by_case_id()
    {
        var report = Report(
            Case(30, "high_court", "DelhiHighCourt", "Pending", "against", "01-01-2025"),
            Case(10, "high_court", "DelhiHighCourt", "Pending", "against", "01-01-2025"),
            Case(20, "high_court", "DelhiHighCourt", "Pending", "against", "01-01-2025"));

        Assert.Equal([10L, 20L, 30L], LitigationReportArtifacts.BuildCaseViews(report).Select(v => v.Item.LitigationCaseId).ToArray());
    }

    [Fact]
    public void The_sort_key_is_what_the_report_orders_by()
    {
        var report = Report(
            Case(1, "tribunal_cases", "Itat", "Pending", "against", "01-01-2020"),
            Case(2, "high_court", "DelhiHighCourt", "Pending", "by", "01-01-2025"),
            Case(3, "high_court", "DelhiHighCourt", "Pending", "against", null));

        var views = LitigationReportArtifacts.BuildCaseViews(report);
        var byKey = views.OrderBy(v => LitigationCasePresentation.SortKey(v.Bucket, v.Institution, v.Side, v.Item.FilingDate, v.Item.LitigationCaseId)).ToList();

        Assert.Equal(byKey.Select(v => v.Item.LitigationCaseId), views.Select(v => v.Item.LitigationCaseId));
        Assert.Equal([3L, 2L, 1L], views.Select(v => v.Item.LitigationCaseId).ToArray()); // HC against (undated), HC by, then ITAT
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
            Case(1, "high_court", "DelhiHighCourt", "Pending", "against", "05-10-2025"),
            Case(2, "high_risk_court", "Drt", "Disposed", "against", "10-03-2016"),
            Case(3, "tribunal_cases", "Itat", "Fixed", null, "05-10-2025"));
        var text = PdfText(report);

        Assert.Contains("PENDING CASES", text);
        Assert.Contains("DISPOSED CASES", text);
        Assert.Contains("NOT REPORTED", text);
        Assert.Contains("FORUM WISE", text);
        Assert.Contains("CASE AGE", text);
        Assert.Contains("Pending for 1 year", text);
        Assert.Contains("Against the company", text);
        Assert.DoesNotContain("csp-1", text);
        Assert.DoesNotContain("CSP", text);
        Assert.DoesNotContain("Retrieval failed", text);
        Assert.DoesNotContain("Retrieval pending", text);
    }

    private static StandaloneReportOrderDto OrderIn(long id, LitigationOrderAvailabilityBucket bucket) =>
        new(id, "10-01-2025", "order/judgement", bucket, null, null, null, "internal pipeline wording", "x", "x");

    [SkippableTheory]
    [InlineData(LitigationOrderAvailabilityBucket.Failed, LitigationOrderAvailabilityBucket.Expired, "0of2orderdocumentsareavailableintheportal.")]
    [InlineData(LitigationOrderAvailabilityBucket.Downloaded, LitigationOrderAvailabilityBucket.Failed, "1of2orderdocumentsareavailableintheportal.")]
    [InlineData(LitigationOrderAvailabilityBucket.Pending, LitigationOrderAvailabilityBucket.Pending, "0of2orderdocumentsareavailableintheportal.")]
    public void Pdf_states_order_availability_as_a_neutral_count_whatever_the_unavailable_orders_state(
        LitigationOrderAvailabilityBucket first, LitigationOrderAvailabilityBucket second, string expectedSquashed)
    {
        Skip.IfNot(OperatingSystem.IsWindows(),
            "PdfPig text extraction is unreliable off Windows fonts; covered by the windows-tests CI job.");

        var one = Case(1, "high_court", "DelhiHighCourt", "Pending", "against", "05-10-2025");
        var report = Report(one with { Orders = [OrderIn(1, first), OrderIn(2, second)] });
        var text = new string(PdfText(report).Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray());

        Assert.Contains(expectedSquashed, text);
        // never implies the unavailable documents are merely awaiting retrieval, and never leaks pipeline wording
        Assert.DoesNotContain("notyetavailable", text);
        Assert.DoesNotContain("internalpipelinewording", text);
        Assert.DoesNotContain("Retrieval", text);
    }

    [Fact]
    public void Pdf_renders_with_and_without_the_logo_and_with_no_cases()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var withCases = Report(Case(1, "high_court", "DelhiHighCourt", "Pending", "against", "05-10-2025"));

        Assert.True(LitigationReportArtifacts.RenderPdf(withCases, png).Length > 1000);
        Assert.True(LitigationReportArtifacts.RenderPdf(withCases, null).Length > 1000);
        Assert.True(LitigationReportArtifacts.RenderPdf(Report(), png).Length > 1000);
    }
}
