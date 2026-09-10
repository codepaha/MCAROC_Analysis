using System.Net;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Caching.Memory;
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
    public async Task Missing_api_key_fails_without_a_network_request()
    {
        var client = NewClient(HttpStatusCode.OK, "{}", "");

        var exception = await Assert.ThrowsAsync<PreLoginReportException>(() => client.GetCompanyAsync("U12345DL2025PTC123456", CancellationToken.None));

        Assert.Contains("not configured", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(PreLoginReportFormat.Sbi, "Legal Cases")]
    [InlineData(PreLoginReportFormat.Prr, "Directors / Signatories")]
    public async Task Selected_format_generates_an_openable_docx(PreLoginReportFormat format, string expectedSection)
    {
        const string payload = """{ "ReportData": { "companyData": { "company":"Example Private Limited" }, "indexChargesData":[], "directorData":[] } }""";
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new PreLoginReportService(NewClient(HttpStatusCode.OK, payload, "configured-key"), new TestEnvironment(ProjectRoot()), cache);

        var draft = await service.PrepareDraftAsync(new PreLoginReportViewModel { Cin = "U12345DL2025PTC123456", Format = format }, CancellationToken.None);
        draft.Company.Name = "Reviewed Example Private Limited";
        var result = await service.GenerateDraftAsync(draft, CancellationToken.None);

        Assert.EndsWith($"_{format.ToString().ToUpperInvariant()}.docx", result.FileName, StringComparison.Ordinal);
        using var document = WordprocessingDocument.Open(new MemoryStream(result.Bytes), false);
        var text = document.MainDocumentPart!.Document!.InnerText;
        Assert.Contains("Reviewed Example Private Limited", text);
        Assert.Contains(expectedSection, text);
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
