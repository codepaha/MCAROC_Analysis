using System.Net;
using System.Text;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class OrderSourceClientAdapterTests
{
    [Fact]
    public async Task DrtAdapter_detects_unsupported_docx_format_and_marks_unsupported()
    {
        var bytes = new byte[] { 0x50, 0x4B, 0x03, 0x04 }.Concat(Encoding.ASCII.GetBytes("word/document.xml")).ToArray();
        var cleaned = DrtOrderCleaner.Clean(bytes);
        Assert.Equal("docx", cleaned.Format);

        var drtMock = new MockDrtClient { DownloadResult = cleaned };
        var adapter = new DrtOrderSourceClient(drtMock);

        var result = await adapter.DownloadAsync(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/order.docx"));

        Assert.True(result.Ok);
        Assert.True(result.IsUnsupportedFormat);
        Assert.Equal(bytes, result.Bytes);
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", result.ContentType);
        Assert.Contains("Word .docx format is not supported", result.Error);
    }

    [Fact]
    public void CanHandle_identifies_matching_source_locators()
    {
        var drtMock = new MockDrtClient();
        var drtAdapter = new DrtOrderSourceClient(drtMock);

        var consumerMock = new MockConsumerClient();
        var consumerAdapter = new ConsumerOrderSourceClient(consumerMock);

        var bprAdapter = new BprOrderSourceClient(
            new BprLitigationClient(new HttpClient(), Options.Create(new BprLitigationOptions()), NullLogger<BprLitigationClient>.Instance));

        // DRT
        Assert.True(drtAdapter.CanHandle(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=123")));
        Assert.True(drtAdapter.CanHandle(new OrderSourceLocator(Source: "DRAT", OrderUrl: "https://drt.gov.in/qrpdfview.php?id=123")));
        Assert.True(drtAdapter.CanHandle(new OrderSourceLocator(Source: "Unknown", OrderUrl: "https://drt.gov.in/pdf1.php?id=123")));
        Assert.False(drtAdapter.CanHandle(new OrderSourceLocator(Source: "Consumer", FilingReferenceNumber: 12345, DateOfHearing: new DateOnly(2022, 1, 1))));

        // Consumer
        Assert.True(consumerAdapter.CanHandle(new OrderSourceLocator(Source: "Consumer", FilingReferenceNumber: 12345, DateOfHearing: new DateOnly(2022, 1, 1))));
        Assert.True(consumerAdapter.CanHandle(new OrderSourceLocator(Source: "NCDRC", FilingReferenceNumber: 12345, DateOfHearing: new DateOnly(2022, 1, 1))));
        Assert.True(consumerAdapter.CanHandle(new OrderSourceLocator(Source: "Unknown", FilingReferenceNumber: 12345, DateOfHearing: new DateOnly(2022, 1, 1))));
        Assert.False(consumerAdapter.CanHandle(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=123")));

        // BPR
        Assert.True(bprAdapter.CanHandle(new OrderSourceLocator(Source: "Bpr", PdfUrl: "https://vendor.com/orders/doc1.pdf")));
        Assert.True(bprAdapter.CanHandle(new OrderSourceLocator(Source: "Unknown", PdfUrl: "https://vendor.com/orders/doc1.pdf")));
        Assert.False(bprAdapter.CanHandle(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=123")));
    }

    [Fact]
    public async Task DrtAdapter_downloads_pdf_order_and_passes_cleaned_bytes()
    {
        var pdfBytes = "%PDF-1.4 sample content"u8.ToArray();
        var drtMock = new MockDrtClient
        {
            DownloadResult = DrtOrderDownloadResult.Success(pdfBytes, "application/pdf", "pdf", cleanedPdf: false)
        };
        var adapter = new DrtOrderSourceClient(drtMock);

        var result = await adapter.DownloadAsync(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=123"));

        Assert.True(result.Ok);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(pdfBytes, result.Bytes);
        Assert.False(result.NotPublished);
        Assert.False(result.IsUnsupportedFormat);
    }

    [Fact]
    public async Task DrtAdapter_detects_unsupported_doc_format_and_marks_unsupported()
    {
        var docBytes = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };
        var drtMock = new MockDrtClient
        {
            DownloadResult = new DrtOrderDownloadResult(true, docBytes, "application/msword", "doc", null, false, false)
        };
        var adapter = new DrtOrderSourceClient(drtMock);

        var result = await adapter.DownloadAsync(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=123"));

        Assert.True(result.Ok);
        Assert.True(result.IsUnsupportedFormat);
        Assert.Equal("application/msword", result.ContentType);
        Assert.Contains("Word .doc format is not supported", result.Error);
    }

    [Fact]
    public async Task DrtAdapter_extracts_embedded_html_from_url_query_parameter()
    {
        var drtMock = new MockDrtClient();
        var adapter = new DrtOrderSourceClient(drtMock);

        var url = "https://drt.gov.in/pdf1.php?file=%3Chtml%3E%3Cbody%3E%3Cp%3EMatter+adjourned+to+15-10-2026%3C%2Fp%3E%3C%2Fbody%3E%3C%2Fhtml%3E";
        var result = await adapter.DownloadAsync(new OrderSourceLocator(Source: "Drt", OrderUrl: url));

        Assert.True(result.Ok);
        Assert.Null(result.Bytes);
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        Assert.Contains("Matter adjourned to 15-10-2026", result.Text);
    }

    [Fact]
    public async Task DrtAdapter_maps_not_available_to_not_published()
    {
        var drtMock = new MockDrtClient
        {
            DownloadResult = DrtOrderDownloadResult.NotAvailable("Order not found (HTTP 404)")
        };
        var adapter = new DrtOrderSourceClient(drtMock);

        var result = await adapter.DownloadAsync(new OrderSourceLocator(Source: "Drt", OrderUrl: "https://drt.gov.in/pdf1.php?id=999"));

        Assert.False(result.Ok);
        Assert.True(result.NotPublished);
        Assert.Contains("not found", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConsumerAdapter_downloads_pdf_order_successfully()
    {
        var pdfBytes = "%PDF-1.4 consumer order"u8.ToArray();
        var consumerMock = new MockConsumerClient
        {
            DownloadResult = ConsumerOrderDownloadResult.Success(
                new ConsumerOrderDocument(100000651664L, new DateOnly(2018, 5, 22), 1, pdfBytes, "pdf", "127.0.0.1:8080"))
        };
        var adapter = new ConsumerOrderSourceClient(consumerMock);

        var locator = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 100000651664L,
            DateOfHearing: new DateOnly(2018, 5, 22),
            OrderTypeId: 1);

        var result = await adapter.DownloadAsync(locator);

        Assert.True(result.Ok);
        Assert.Equal("application/pdf", result.ContentType);
        Assert.Equal(pdfBytes, result.Bytes);
        Assert.Null(result.Text);
    }

    [Fact]
    public async Task ConsumerAdapter_converts_html_notes_to_plain_text()
    {
        var htmlContent = "<html><body><div>Matter dismissed for default.</div></body></html>";
        var htmlBytes = Encoding.UTF8.GetBytes(htmlContent);
        var consumerMock = new MockConsumerClient
        {
            DownloadResult = ConsumerOrderDownloadResult.Success(
                new ConsumerOrderDocument(100000651664L, new DateOnly(2018, 5, 22), 1, htmlBytes, "html", "127.0.0.1:8080"))
        };
        var adapter = new ConsumerOrderSourceClient(consumerMock);

        var locator = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 100000651664L,
            DateOfHearing: new DateOnly(2018, 5, 22));

        var result = await adapter.DownloadAsync(locator);

        Assert.True(result.Ok);
        Assert.Null(result.Bytes);
        Assert.Equal("text/plain; charset=utf-8", result.ContentType);
        Assert.Contains("Matter dismissed for default.", result.Text);
    }

    [Fact]
    public async Task ConsumerAdapter_maps_http_400_not_available_to_not_published()
    {
        var consumerMock = new MockConsumerClient
        {
            DownloadResult = ConsumerOrderDownloadResult.NotFound("Daily Order not available")
        };
        var adapter = new ConsumerOrderSourceClient(consumerMock);

        var locator = new OrderSourceLocator(
            Source: "Consumer",
            FilingReferenceNumber: 100000651664L,
            DateOfHearing: new DateOnly(2018, 5, 22));

        var result = await adapter.DownloadAsync(locator);

        Assert.False(result.Ok);
        Assert.True(result.NotPublished);
        Assert.Contains("not available", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MockDrtClient : IDrtClient
    {
        public string CurrentExit => "127.0.0.1:8080";
        public DrtOrderDownloadResult DownloadResult { get; set; } = DrtOrderDownloadResult.NotAvailable();

        public Task<IReadOnlyList<DrtTribunal>> GetTribunalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtTribunal>>([new(1, "DRT 1 Delhi", false, "Delhi", "Delhi", "DRT 1")]);

        public Task<IReadOnlyList<DrtCaseType>> GetCaseTypesAsync(int tribunalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtCaseType>>([]);

        public Task<IReadOnlyList<DrtPartySearchResult>> SearchByPartyNameAsync(int tribunalId, string partyName, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtPartySearchResult>>([]);

        public Task<DrtCaseLookupResult> GetCaseByNumberAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<DrtCaseLookupResult> GetCaseByDiaryAsync(int tribunalId, string diaryNo, int diaryYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<IReadOnlyList<DrtOrder>> GetDailyOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<IReadOnlyList<DrtOrder>> GetFinalOrdersAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DrtOrder>>([]);

        public Task<DrtCaseLookupResult> GetRcTrcCaseAsync(int tribunalId, int caseTypeId, string caseNo, int caseYear, CancellationToken ct = default) =>
            Task.FromResult(DrtCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<DrtOrderDownloadResult> DownloadOrderAsync(string orderUrl, long maxBytes = 20971520, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult);
    }

    private sealed class MockConsumerClient : IConsumerClient
    {
        public string CurrentExit => "127.0.0.1:8080";
        public ConsumerProxyRotator Rotator => new([]);
        public ConsumerOrderDownloadResult DownloadResult { get; set; } = ConsumerOrderDownloadResult.NotFound();

        public Task<ConsumerCaseLookupResult> GetCaseStatusAsync(string caseNumber, CancellationToken ct = default) =>
            Task.FromResult(ConsumerCaseLookupResult.Error("Not implemented", null, HttpStatusCode.NotFound));

        public Task<ConsumerCompanySearchResult> SearchByCompanyAsync(int commissionTypeId, int commissionId, string keyword, DateOnly fromDate, DateOnly toDate, CancellationToken ct = default) =>
            Task.FromResult(new ConsumerCompanySearchResult([], true));

        public Task<ConsumerOrderDownloadResult> GetDailyOrderPdfAsync(long filingReferenceNumber, DateOnly dateOfHearing, int orderTypeId = 1, CancellationToken ct = default) =>
            Task.FromResult(DownloadResult);
    }
}

