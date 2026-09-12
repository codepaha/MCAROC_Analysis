using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class InstaFinancialsClientTests
{
    [Fact]
    public async Task CompanyCin_response_is_normalized_for_report_generation()
    {
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited", "rocName":"ROC Delhi", "authorisedCapital":100000, "paidUpCapital":50000, "whetherListedOrNot":"N", "dateOfIncorporation":"2025-09-09", "MCAMDSCompanyAddress":[{"addressType":"Registered","streetAddress":"10 Main Road","city":"Delhi"}] }, "indexChargesData":[{"chargeId":"123","chName":"Example Bank","amount":250000,"chargeStatus":"open","dateOfCreation":"2025-01-02"}], "directorData":[{"FirstName":"JANE","LastName":"DOE","DIN":"01234567","dateOfAppointment":"2025-01-03","MCAUserRole":[{"cin":"U12345DL2025PTC123456","designation":"Director"}]}] } }
        """;
        var client = NewClient(HttpStatusCode.OK, payload, "configured-key");

        var result = await client.GetCompanyAsync("U12345DL2025PTC123456", CancellationToken.None);

        Assert.Equal("Example Private Limited", result.Company.Name);
        Assert.Equal("100,000", result.Company.AuthorisedCapital);
        Assert.Equal("Unlisted", result.Company.Listed);
        Assert.Equal("10 Main Road, Delhi", result.Company.Address);
        var charge = Assert.Single(result.Charges);
        Assert.True(charge.IsOpen);
        Assert.Equal("02-01-2025", charge.Created);
        var director = Assert.Single(result.Directors);
        Assert.Equal("Jane Doe", director.Name);
        Assert.Equal("Director", director.Designation);
    }

    [Fact]
    public async Task Duplicate_director_role_records_for_the_same_person_collapse_to_one_row()
    {
        // InstaBasic's directorData is one row per historical role event against the company, not one row
        // per person — a director re-appointed under a new designation later shows up twice with the same
        // DIN. Confirmed against live data (58-charge company, DIN 02103971 "Sanjiv Kumar Jain": an earlier
        // "Whole-time director" role from 2013, and a later blank-designation "Director/Designated Partner"
        // role from 2023). The most recent appointment should win, and its designation should resolve via
        // the roleLICValue fallback rather than showing blank.
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited" }, "indexChargesData":[], "directorData":[
          {"FirstName":"SANJIV","MiddleName":"KUMAR","LastName":"JAIN","DIN":"02103971","dateOfAppointment":"2013-09-26",
           "MCAUserRole":[{"cin":"U12345DL2025PTC123456","designation":"Whole-time director","roleLICValue":"Wholetime Director"}]},
          {"FirstName":"SANJIV","MiddleName":"KUMAR","LastName":"JAIN","DIN":"02103971","dateOfAppointment":"2023-02-22",
           "MCAUserRole":[{"cin":"U12345DL2025PTC123456","designation":"","roleLICValue":"Director/Designated Partner"}]}
        ] } }
        """;
        var client = NewClient(HttpStatusCode.OK, payload, "configured-key");

        var result = await client.GetCompanyAsync("U12345DL2025PTC123456", CancellationToken.None);

        var director = Assert.Single(result.Directors);
        Assert.Equal("Sanjiv Kumar Jain", director.Name);
        Assert.Equal("22-02-2023", director.Appointed);
        Assert.Equal("Director/Designated Partner", director.Designation);
    }

    [Fact]
    public async Task Missing_api_key_fails_without_a_network_request()
    {
        var client = NewClient(HttpStatusCode.OK, "{}", "");

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => client.GetCompanyAsync("U12345DL2025PTC123456", CancellationToken.None));

        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_a_draft_to_clear_directors_still_generates_an_sbi_report()
    {
        // Directors editing only applies to Prr in the UI — for Sbi the section isn't shown, so an edit
        // that clears Directors (or never touches it) must still generate fine. There's no longer a
        // tamper-check tying the draft back to the original fetch — editing (including changing counts)
        // is the whole point of the optional edit-from-History flow.
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited" }, "indexChargesData":[],
          "directorData":[{"FirstName":"JANE","LastName":"DOE","DIN":"01234567","dateOfAppointment":"2025-01-03",
          "MCAUserRole":[{"cin":"U12345DL2025PTC123456","designation":"Director"}]}] } }
        """;
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()));

        var data = await service.FetchDataAsync("U12345DL2025PTC123456", null, CancellationToken.None);
        var draft = PreLoginReportService.ToDraft(1, Guid.NewGuid(), "U12345DL2025PTC123456", PreLoginReportFormat.Sbi, data);
        Assert.NotEmpty(draft.Directors);
        draft.Directors.Clear();

        var result = await service.GenerateFromDataAsync(draft.Cin, draft.Format, PreLoginReportService.ApplyEdits(draft), CancellationToken.None);

        Assert.EndsWith("_SBI.docx", result.FileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PreLoginReportFormat.Sbi, "Legal Cases")]
    [InlineData(PreLoginReportFormat.Prr, "Directors / Signatories")]
    public async Task Selected_format_generates_an_openable_docx(PreLoginReportFormat format, string expectedSection)
    {
        const string payload = """{ "ReportData": { "companyData": { "company":"Example Private Limited" }, "indexChargesData":[], "directorData":[] } }""";
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()));

        var data = await service.FetchDataAsync("U12345DL2025PTC123456", null, CancellationToken.None);
        var draft = PreLoginReportService.ToDraft(1, Guid.NewGuid(), "U12345DL2025PTC123456", format, data);
        draft.Company.Name = "Reviewed Example Private Limited";
        var result = await service.GenerateFromDataAsync(draft.Cin, draft.Format, PreLoginReportService.ApplyEdits(draft), CancellationToken.None);

        Assert.EndsWith($"_{format.ToString().ToUpperInvariant()}.docx", result.FileName, StringComparison.Ordinal);
        using var document = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = document.MainDocumentPart!.Document!.InnerText;
        Assert.Contains("Reviewed Example Private Limited", text);
        Assert.Contains(expectedSection, text);
    }

    [Fact]
    public async Task Sbi_report_fills_the_real_template_and_leaves_no_trace_of_the_sample_company()
    {
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited", "rocName":"ROC Delhi" }, "indexChargesData":[], "directorData":[] } }
        """;
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()));

        var data = await service.FetchDataAsync("U12345DL2025PTC123456", null, CancellationToken.None);
        var draft = PreLoginReportService.ToDraft(1, Guid.NewGuid(), "U12345DL2025PTC123456", PreLoginReportFormat.Sbi, data);
        draft.Company.ActiveCompliance = "Yes";
        draft.Company.BooksOfAccountAddress = "10 Main Road, Delhi";
        var result = await service.GenerateFromDataAsync(draft.Cin, draft.Format, PreLoginReportService.ApplyEdits(draft), CancellationToken.None);

        using var document = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        // .InnerText does not reliably surface text inside content controls (SdtRun) — read raw <w:t> runs
        // directly instead, matching how this exact bug (a leftover content control duplicating text beside
        // a newly-appended run) was actually found and confirmed fixed.
        var text = string.Concat(document.MainDocumentPart!.Document!.Body!.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().Select(t => t.Text));

        // The 2 fields present in the template but absent from InstaBasic data.
        Assert.Contains("Address at which the books of account are to be maintained", text);
        Assert.Contains("10 Main Road, Delhi", text);
        Assert.Contains("ACTIVE compliance", text);
        Assert.Contains("Yes", text);
        // The 9-column Legal Cases layout (extended from the template's original 6).
        Assert.Contains("NCLT/NCLAT", text);
        Assert.Contains("DRT/DRAT", text);
        Assert.Contains("RERA", text);
        // All 3 occurrences of the sample company name (plain cell + 2 content controls) must be gone.
        Assert.DoesNotContain("BARROD SOLAR ENERGY PRIVATE LIMITED", text);
        // Exactly 3, not 4 — a leftover, un-cleared content control beside a freshly-appended run would
        // duplicate the name in the SUMMARY line without technically containing the old sample name.
        Assert.Equal(3, Regex.Matches(text, Regex.Escape("Example Private Limited")).Count);
        Assert.Contains("U12345DL2025PTC123456", text);
    }

    [Fact]
    public async Task Sbi_report_clones_a_charges_row_per_charge()
    {
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited" }, "directorData":[], "indexChargesData":[
          {"chargeId":"111","chName":"First Bank","amount":100000,"chargeStatus":"open","dateOfCreation":"2025-01-02"},
          {"chargeId":"222","chName":"Second Bank","amount":200000,"chargeStatus":"open","dateOfCreation":"2025-03-04"}
        ] } }
        """;
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()));

        var data = await service.FetchDataAsync("U12345DL2025PTC123456", null, CancellationToken.None);
        var draft = PreLoginReportService.ToDraft(1, Guid.NewGuid(), "U12345DL2025PTC123456", PreLoginReportFormat.Sbi, data);
        var result = await service.GenerateFromDataAsync(draft.Cin, draft.Format, PreLoginReportService.ApplyEdits(draft), CancellationToken.None);

        using var document = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = document.MainDocumentPart!.Document!.InnerText;
        Assert.Contains("First Bank", text);
        Assert.Contains("Second Bank", text);
        Assert.Contains("111", text);
        Assert.Contains("222", text);
    }

    [Fact]
    public async Task Editing_can_add_a_charge_that_was_never_in_the_original_fetch()
    {
        // Directly exercises the "add a charge row while editing from History" capability: the API
        // returned 1 charge, but the edited draft adds a 2nd one that was never fetched — both must render.
        const string payload = """
        { "ReportData": { "companyData": { "company":"Example Private Limited" }, "directorData":[], "indexChargesData":[
          {"chargeId":"111","chName":"First Bank","amount":100000,"chargeStatus":"open","dateOfCreation":"2025-01-02"}
        ] } }
        """;
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()));

        var data = await service.FetchDataAsync("U12345DL2025PTC123456", null, CancellationToken.None);
        var draft = PreLoginReportService.ToDraft(1, Guid.NewGuid(), "U12345DL2025PTC123456", PreLoginReportFormat.Sbi, data);
        Assert.Single(draft.Charges);
        draft.Charges.Add(new EditableChargeViewModel { Id = "999", Holder = "Manually Added Bank", Created = "2025-06-01", Amount = "500000", IsOpen = true });

        var result = await service.GenerateFromDataAsync(draft.Cin, draft.Format, PreLoginReportService.ApplyEdits(draft), CancellationToken.None);

        using var document = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = document.MainDocumentPart!.Document!.InnerText;
        Assert.Contains("First Bank", text);
        Assert.Contains("Manually Added Bank", text);
        Assert.Contains("999", text);
    }

    private static InstaFinancialsClient NewClient(HttpStatusCode statusCode, string body, string apiKey) => new(
        new HttpClient(new StubHandler(statusCode, body)),
        Options.Create(new InstaFinancialsOptions { ApiKey = apiKey }));

    private sealed class StubHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    private static string ProjectRoot() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../MCAROC_Analysis"));

    private sealed class TestEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}
