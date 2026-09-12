using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MCAROC_Analysis.Tests;

public class DocumentViewerTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly List<string> _tempFiles = new();

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static RequestsController NewController(AppDbContext db)
    {
        var controller = new RequestsController(db, null!, null!, null!, null!, null!, Dossier.DossierGoldenMasterTests.CreateCache(), null!, null!);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        return controller;
    }

    private static async Task<IHost> CreateTestHostAsync()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddDbContext<AppDbContext>(options => options.UseSqlServer(ConnectionString));
                        services.AddRouting();
                        services.AddTransient<RequestsController>(sp => NewController(sp.GetRequiredService<AppDbContext>()));
                        services.AddControllers()
                            .AddApplicationPart(typeof(RequestsController).Assembly)
                            .AddControllersAsServices();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapControllers();
                        });
                    });
            });

        return await host.StartAsync();
    }

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch { }
        }
        return Task.CompletedTask;
    }

    private string CreateTempPdfFile(bool validSignature = true)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcaroc_test_{Guid.NewGuid():N}.pdf");
        var content = validSignature
            ? new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x25, 0x45, 0x4F, 0x46 } // %PDF-1.7\n%EOF
            : new byte[] { 0x4E, 0x4F, 0x54, 0x50, 0x44, 0x46, 0x21 }; // NOTPDF!
        File.WriteAllBytes(tempPath, content);
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    private static async Task<(long RequestId, long BatchId, long FilingId)> SeedRequestAndBatchAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Viewer Test Ltd",
            RequestNumber = $"REQ-VIEW-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = request.RequestId,
            BatchId = batch.BatchId,
            Srn = "SRN123456",
            ParsedCompanyName = "Viewer Test Ltd"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        return (request.RequestId, batch.BatchId, filing.FilingId);
    }

    [Fact]
    public async Task Scoping_mismatch_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req1, batch1, filing1) = await SeedRequestAndBatchAsync(db);
        var (req2, _, _) = await SeedRequestAndBatchAsync(db);

        var path = CreateTempPdfFile(validSignature: true);
        var doc = new McaFilingDocument
        {
            RequestId = req1,
            BatchId = batch1,
            FilingId = filing1,
            OriginalFileName = "report.pdf",
            StoragePath = path,
            Category = FilingCategory.Charge
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = NewController(db);

        // Attempt to access document belonging to req1 under req2
        var viewResult = await controller.DocumentView(req2, doc.FilingDocumentId, CancellationToken.None);
        var rawResult = await controller.DocumentRaw(req2, doc.FilingDocumentId, CancellationToken.None);
        var dlResult = await controller.DocumentDownload(req2, doc.FilingDocumentId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(viewResult);
        Assert.IsType<NotFoundResult>(rawResult);
        Assert.IsType<NotFoundResult>(dlResult);
    }

    [Fact]
    public async Task NonExistent_document_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req, _, _) = await SeedRequestAndBatchAsync(db);
        var controller = NewController(db);

        var result = await controller.DocumentRaw(req, 999_999_999, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Missing_physical_file_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "missing.pdf",
            StoragePath = Path.Combine(Path.GetTempPath(), $"nonexistent_{Guid.NewGuid():N}.pdf"),
            Category = FilingCategory.Charge
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req, doc.FilingDocumentId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Corrupt_signature_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var corruptFile = CreateTempPdfFile(validSignature: false);
        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "corrupt.pdf",
            StoragePath = corruptFile,
            Category = FilingCategory.Charge
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req, doc.FilingDocumentId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Canonical_document_returns_PhysicalFile_with_byte_range_enabled()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var pdfPath = CreateTempPdfFile(validSignature: true);
        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "annual_return.pdf",
            StoragePath = pdfPath,
            Category = FilingCategory.Compliance
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req, doc.FilingDocumentId, CancellationToken.None);

        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal("application/pdf", fileResult.ContentType);
        Assert.True(fileResult.EnableRangeProcessing);
        Assert.Equal(pdfPath, fileResult.FileName);
    }

    [Fact]
    public async Task Duplicate_document_resolves_canonical_physical_file()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var pdfPath = CreateTempPdfFile(validSignature: true);
        var canonical = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "canonical.pdf",
            StoragePath = pdfPath,
            Category = FilingCategory.Compliance,
            DuplicateOfDocumentId = null
        };
        db.McaFilingDocuments.Add(canonical);
        await db.SaveChangesAsync();

        var duplicate = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "duplicate_attachment.pdf",
            StoragePath = "", // Storage path empty or bypassed
            DuplicateOfDocumentId = canonical.FilingDocumentId,
            Category = FilingCategory.Compliance
        };
        db.McaFilingDocuments.Add(duplicate);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req, duplicate.FilingDocumentId, CancellationToken.None);

        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(pdfPath, fileResult.FileName);
    }

    [Fact]
    public async Task Chained_duplicate_pointer_fails_closed_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var pdfPath = CreateTempPdfFile(validSignature: true);
        var canonical = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "root.pdf",
            StoragePath = pdfPath
        };
        db.McaFilingDocuments.Add(canonical);
        await db.SaveChangesAsync();

        var intermediate = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "intermediate.pdf",
            DuplicateOfDocumentId = canonical.FilingDocumentId
        };
        db.McaFilingDocuments.Add(intermediate);
        await db.SaveChangesAsync();

        var chained = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "chained.pdf",
            DuplicateOfDocumentId = intermediate.FilingDocumentId // Points to another duplicate!
        };
        db.McaFilingDocuments.Add(chained);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req, chained.FilingDocumentId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Cross_request_or_cross_batch_duplicate_returns_NotFound()
    {
        await using var db = CreateContext();
        var (req1, batch1, filing1) = await SeedRequestAndBatchAsync(db);
        var (req2, batch2, filing2) = await SeedRequestAndBatchAsync(db);

        var pdfPath = CreateTempPdfFile(validSignature: true);
        var canonical1 = new McaFilingDocument
        {
            RequestId = req1,
            BatchId = batch1,
            FilingId = filing1,
            OriginalFileName = "req1_canon.pdf",
            StoragePath = pdfPath
        };
        db.McaFilingDocuments.Add(canonical1);
        await db.SaveChangesAsync();

        // Cross-request pointer
        var crossReqDoc = new McaFilingDocument
        {
            RequestId = req2,
            BatchId = batch2,
            FilingId = filing2,
            OriginalFileName = "cross_req.pdf",
            DuplicateOfDocumentId = canonical1.FilingDocumentId
        };
        db.McaFilingDocuments.Add(crossReqDoc);
        await db.SaveChangesAsync();

        var controller = NewController(db);
        var result = await controller.DocumentRaw(req2, crossReqDoc.FilingDocumentId, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Response_headers_and_content_disposition_are_safe_and_explicit()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var pdfPath = CreateTempPdfFile(validSignature: true);
        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "Quarterly Report 2026.pdf",
            StoragePath = pdfPath,
            Category = FilingCategory.Financial
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        // 1. Raw endpoint
        var rawController = NewController(db);
        var rawResult = await rawController.DocumentRaw(req, doc.FilingDocumentId, CancellationToken.None);
        Assert.IsType<PhysicalFileResult>(rawResult);

        var rawHeaders = rawController.Response.Headers;
        Assert.Equal("no-store, private", rawHeaders.CacheControl.ToString());
        Assert.Equal("nosniff", rawHeaders["X-Content-Type-Options"].ToString());
        Assert.Contains("inline", rawHeaders.ContentDisposition.ToString());
        Assert.Contains($"document-{doc.FilingDocumentId}.pdf", rawHeaders.ContentDisposition.ToString());

        // 2. Download endpoint
        var dlController = NewController(db);
        var dlResult = await dlController.DocumentDownload(req, doc.FilingDocumentId, CancellationToken.None);
        Assert.IsType<PhysicalFileResult>(dlResult);

        var dlHeaders = dlController.Response.Headers;
        Assert.Equal("no-store, private", dlHeaders.CacheControl.ToString());
        Assert.Equal("nosniff", dlHeaders["X-Content-Type-Options"].ToString());
        Assert.Contains("attachment", dlHeaders.ContentDisposition.ToString());
        Assert.Contains("Quarterly Report 2026.pdf", dlHeaders.ContentDisposition.ToString());

        // 3. View endpoint
        var viewController = NewController(db);
        var viewResult = await viewController.DocumentView(req, doc.FilingDocumentId, CancellationToken.None);
        var vr = Assert.IsType<ViewResult>(viewResult);
        var vm = Assert.IsType<DocumentViewerViewModel>(vr.Model);
        Assert.Equal("DocumentViewer", vr.ViewName);
        Assert.Equal(doc.OriginalFileName, vm.OriginalFileName);
        Assert.Equal($"/Requests/{req}/documents/{doc.FilingDocumentId}.pdf", vm.RawPdfUrl);
        Assert.Equal($"/Requests/{req}/documents/{doc.FilingDocumentId}/download", vm.DownloadUrl);

        var viewHeaders = viewController.Response.Headers;
        Assert.Equal("no-store, private", viewHeaders.CacheControl.ToString());
        Assert.Equal("nosniff", viewHeaders["X-Content-Type-Options"].ToString());
        Assert.Contains("default-src 'none'", viewHeaders["Content-Security-Policy"].ToString());
        Assert.Contains("script-src 'self' 'wasm-unsafe-eval'", viewHeaders["Content-Security-Policy"].ToString());
        Assert.Contains("worker-src 'self' blob:", viewHeaders["Content-Security-Policy"].ToString());
    }

    [Theory]
    [InlineData("../../etc/passwd", "passwd.pdf")]
    [InlineData("..\\..\\windows\\system32\\cmd.exe", "cmd.exe.pdf")]
    [InlineData("test\r\nSet-Cookie: evil=1\r\n.pdf", "testSet-Cookie evil=1.pdf")]
    [InlineData("bad\"quote.pdf", "badquote.pdf")]
    [InlineData("", "document-42.pdf")]
    [InlineData("   ", "document-42.pdf")]
    [InlineData(".pdf", "document-42.pdf")]
    public void GetSafeDownloadFileName_sanitizes_hostile_input(string hostileInput, string expected)
    {
        var safe = RequestsController.GetSafeDownloadFileName(42, hostileInput);
        Assert.Equal(expected, safe);
    }

    [Fact]
    public void PdfJs_vendored_assets_smoke_test()
    {
        var root = AppContext.BaseDirectory;
        // Search upwards for wwwroot/lib/pdfjs
        var dir = new DirectoryInfo(root);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "lib", "pdfjs")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var pdfjsDir = Path.Combine(dir.FullName, "MCAROC.Portal", "MCAROC_Analysis", "wwwroot", "lib", "pdfjs");
        Assert.True(Directory.Exists(pdfjsDir), $"PDF.js vendored directory missing: {pdfjsDir}");

        // Check required files
        Assert.True(File.Exists(Path.Combine(pdfjsDir, "pdf.mjs")));
        Assert.True(File.Exists(Path.Combine(pdfjsDir, "pdf.worker.mjs")));
        Assert.True(File.Exists(Path.Combine(pdfjsDir, "pdf_viewer.css")));
        Assert.True(File.Exists(Path.Combine(pdfjsDir, "LICENSE")));
        Assert.True(File.Exists(Path.Combine(pdfjsDir, "README.md")));

        // Check required subdirectories
        var cmapsDir = Path.Combine(pdfjsDir, "cmaps");
        Assert.True(Directory.Exists(cmapsDir));
        Assert.True(Directory.GetFiles(cmapsDir, "*.bcmap").Length >= 100);

        var fontsDir = Path.Combine(pdfjsDir, "standard_fonts");
        Assert.True(Directory.Exists(fontsDir));
        Assert.True(Directory.GetFiles(fontsDir).Length >= 10);

        var wasmDir = Path.Combine(pdfjsDir, "wasm");
        Assert.True(Directory.Exists(wasmDir));
        Assert.True(Directory.GetFiles(wasmDir, "*.wasm").Length >= 1);
    }

    [Fact]
    public async Task Http_Range_request_returns_206_PartialContent_with_exact_byte_slice_and_content_range()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        // Create a 16-byte PDF file: %PDF-1.7\n12345\n%
        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x31, 0x32, 0x33, 0x34, 0x35, 0x0A, 0x25 };
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcaroc_range_{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tempPath, pdfBytes);
        _tempFiles.Add(tempPath);

        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "range_test.pdf",
            StoragePath = tempPath,
            Category = FilingCategory.Compliance
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        // 1. Request byte slice 0-4 (first 5 bytes "%PDF-")
        using var request1 = new HttpRequestMessage(HttpMethod.Get, $"/Requests/{req}/documents/{doc.FilingDocumentId}.pdf");
        request1.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 4);

        using var response1 = await client.SendAsync(request1);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response1.StatusCode);
        Assert.Equal("application/pdf", response1.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response1.Content.Headers.ContentRange);
        Assert.Equal(0, response1.Content.Headers.ContentRange.From);
        Assert.Equal(4, response1.Content.Headers.ContentRange.To);
        Assert.Equal(pdfBytes.Length, response1.Content.Headers.ContentRange.Length);
        Assert.Equal(5, response1.Content.Headers.ContentLength);

        var body1 = await response1.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdfBytes[..5], body1);

        // Security headers survive result execution
        Assert.Equal("no-store, private", response1.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response1.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
        var cd1 = response1.Content.Headers.ContentDisposition?.ToString()
            ?? (response1.Headers.TryGetValues("Content-Disposition", out var cdVals) ? cdVals.FirstOrDefault() : null);
        Assert.Contains("inline", cd1);

        // 2. Request middle slice 5-10
        using var request2 = new HttpRequestMessage(HttpMethod.Get, $"/Requests/{req}/documents/{doc.FilingDocumentId}.pdf");
        request2.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(5, 10);

        using var response2 = await client.SendAsync(request2);
        Assert.Equal(System.Net.HttpStatusCode.PartialContent, response2.StatusCode);
        Assert.Equal("bytes 5-10/16", response2.Content.Headers.ContentRange?.ToString());
        Assert.Equal(6, response2.Content.Headers.ContentLength);

        var body2 = await response2.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdfBytes[5..11], body2);
    }

    [Fact]
    public async Task Http_Full_request_returns_200_OK_with_entire_file_and_headers()
    {
        await using var db = CreateContext();
        var (req, batch, filing) = await SeedRequestAndBatchAsync(db);

        var pdfBytes = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x37, 0x0A, 0x25, 0x45, 0x4F, 0x46 };
        var tempPath = Path.Combine(Path.GetTempPath(), $"mcaroc_full_{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(tempPath, pdfBytes);
        _tempFiles.Add(tempPath);

        var doc = new McaFilingDocument
        {
            RequestId = req,
            BatchId = batch,
            FilingId = filing,
            OriginalFileName = "full_test.pdf",
            StoragePath = tempPath,
            Category = FilingCategory.Financial
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        using var host = await CreateTestHostAsync();
        using var client = host.GetTestClient();

        using var response = await client.GetAsync($"/Requests/{req}/documents/{doc.FilingDocumentId}.pdf");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(pdfBytes.Length, response.Content.Headers.ContentLength);

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(pdfBytes, body);

        // Security headers
        Assert.Equal("no-store, private", response.Headers.CacheControl?.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").FirstOrDefault());
    }
}
