using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using NPOI.XSSF.UserModel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class UploadedDocumentDownloadTests : IAsyncLifetime, IDisposable
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mcaroc_download_tests_{Guid.NewGuid():N}");

    public UploadedDocumentDownloadTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    private AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private RequestsController CreateController(AppDbContext db, bool isInternalReviewer = false)
    {
        var env = new FakeEnv(_tempDir);
        var fileVal = new FileValidationService(new ExcelSheetReader());
        var derivService = new WorkbookDerivativeService(db, env, fileVal, NullLogger<WorkbookDerivativeService>.Instance);
        var controller = new RequestsController(
            db: db,
            orchestrator: null!,
            fileValidation: fileVal,
            analysisQueue: null!,
            filingQueue: null!,
            requestListQueryService: null!,
            dossierCache: Dossier.DossierGoldenMasterTests.CreateCache(),
            env: env,
            corporateTimelineBuilder: new CorporateTimelineBuilder(db),
            logger: NullLogger<RequestsController>.Instance,
            derivativeService: derivService);

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthService(isInternalReviewer));
        var serviceProvider = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceProvider
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };

        return controller;
    }

    private static string CreateSyntheticWorkbook(string filePath)
    {
        using (var wb = new XSSFWorkbook())
        {
            var sheet = wb.CreateSheet("About the Company");
            var r0 = sheet.CreateRow(0);
            r0.CreateCell(0).SetCellValue("Legal Name");
            r0.CreateCell(1).SetCellValue("Sample Co");

            var r1 = sheet.CreateRow(1);
            r1.CreateCell(0).SetCellValue("Printed at");
            r1.CreateCell(1).SetCellValue("15 Sep 2026");

            using var fs = File.Create(filePath);
            wb.Write(fs);
        }

        using var readStream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(readStream));
    }

    [Fact]
    public void SafeUploadedFileName_SanitizesTraversalAndSpecialCharacters()
    {
        Assert.Equal("report.xlsx", RequestsController.GetSafeUploadedDownloadFileName(1, "report.xlsx"));
        Assert.Equal("report.xlsx", RequestsController.GetSafeUploadedDownloadFileName(2, @"..\..\secret\report.xlsx"));
        Assert.Equal("report.xlsx", RequestsController.GetSafeUploadedDownloadFileName(3, "../../secret/report.xlsx"));
        Assert.Equal("reportspecialname.xlsx", RequestsController.GetSafeUploadedDownloadFileName(4, "report:special*name?.xlsx"));
        Assert.Equal("document-5.bin", RequestsController.GetSafeUploadedDownloadFileName(5, "   "));
        Assert.Equal("document-6.bin", RequestsController.GetSafeUploadedDownloadFileName(6, null));
        Assert.Equal("कंपनी_डाटा.xlsx", RequestsController.GetSafeUploadedDownloadFileName(7, "कंपनी_डाटा.xlsx"));
    }

    [Fact]
    public async Task DownloadUploadedDocument_RequestScopingMismatch_ReturnsNotFound()
    {
        await using var db = CreateContext();

        var req1 = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Req 1", RequestNumber = $"REQ1-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        var req2 = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Req 2", RequestNumber = $"REQ2-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.AddRange(req1, req2);
        await db.SaveChangesAsync();

        var doc = new RequestDocument
        {
            RequestId = req1.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "test.xlsx",
            StoragePath = "some/path",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        // Requesting doc of Req1 under Req2 route
        var result = await controller.DownloadUploadedDocument(req2.RequestId, doc.DocumentId, default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DownloadUploadedDocument_QuarantinedDocument_DeniedForAnonymous_AllowedForReviewer()
    {
        await using var db = CreateContext();

        var req = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Quarantine Co", RequestNumber = $"QRT-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", req.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "quarantine_sample.csv");
        await File.WriteAllTextAsync(rawPath, "Col1,Col2\nVal1,Val2");

        var doc = new RequestDocument
        {
            RequestId = req.RequestId,
            DocumentType = DocumentType.Other,
            OriginalFileName = "quarantine_sample.csv",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            UploadStatus = DocumentUploadStatus.Quarantined,
            QuarantineReason = "CIN does not match",
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        // 1. Anonymous / non-reviewer should receive 404
        var anonController = CreateController(db, isInternalReviewer: false);
        var anonResult = await anonController.DownloadUploadedDocument(req.RequestId, doc.DocumentId, default);
        Assert.IsType<NotFoundResult>(anonResult);

        // 2. Reviewer should receive 200 PhysicalFileResult
        var reviewerController = CreateController(db, isInternalReviewer: true);
        var reviewerResult = await reviewerController.DownloadUploadedDocument(req.RequestId, doc.DocumentId, default);
        var fileResult = Assert.IsType<PhysicalFileResult>(reviewerResult);
        Assert.Equal(rawPath, fileResult.FileName);
        Assert.Equal("text/csv", fileResult.ContentType);
    }

    [Fact]
    public async Task DownloadUploadedDocument_UnsupportedExtension_Returns415()
    {
        await using var db = CreateContext();

        var req = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Ext Co", RequestNumber = $"EXT-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var doc = new RequestDocument
        {
            RequestId = req.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "malicious.exe",
            StoragePath = "some/path.exe",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.DownloadUploadedDocument(req.RequestId, doc.DocumentId, default);

        var statusResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, statusResult.StatusCode);
    }

    [Fact]
    public async Task DownloadUploadedDocument_ExcelDocument_ServesSanitizedDerivative()
    {
        await using var db = CreateContext();

        var req = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Excel Download Co", RequestNumber = $"EXD-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", req.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "roc_report.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = req.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Coastal_ROC.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.DownloadUploadedDocument(req.RequestId, doc.DocumentId, default);

        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.NotEqual(rawPath, fileResult.FileName);
        Assert.Contains("derivatives", fileResult.FileName);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileResult.ContentType);

        // Verify headers
        var headers = controller.Response.Headers;
        Assert.Equal("no-store, private", headers.CacheControl.ToString());
        Assert.Equal("nosniff", headers["X-Content-Type-Options"].ToString());
        Assert.Contains("attachment", headers.ContentDisposition.ToString());
        Assert.Contains("Coastal_ROC.xlsx", headers.ContentDisposition.ToString());

        // Verify content served is sanitized
        var reader = new ExcelSheetReader();
        var sheets = reader.ReadWorkbook(fileResult.FileName);
        var aboutSheet = sheets.First(s => s.Name == "About the Company");
        var labels = aboutSheet.Rows.Select(r => r.Count > 0 ? r[0]?.ToString()?.Trim() : null).ToList();
        Assert.DoesNotContain(labels, l => l == "Printed at");
    }

    [Fact]
    public async Task DownloadUploadedDocument_NonExcelDocument_ServesOriginalRawFile()
    {
        await using var db = CreateContext();

        var req = new McaRequest { ClientId = 1, EntityType = EntityType.Company, CompanyName = "Zip Download Co", RequestNumber = $"ZIP-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", req.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "filings.zip");
        await File.WriteAllBytesAsync(rawPath, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

        var doc = new RequestDocument
        {
            RequestId = req.RequestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "Filing_Archive.zip",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.DownloadUploadedDocument(req.RequestId, doc.DocumentId, default);

        var fileResult = Assert.IsType<PhysicalFileResult>(result);
        Assert.Equal(rawPath, fileResult.FileName);
        Assert.Equal("application/zip", fileResult.ContentType);
        Assert.Contains("Filing_Archive.zip", controller.Response.Headers.ContentDisposition.ToString());
    }

    [Fact]
    public async Task DocumentsTab_RendersDownloadButtons_AndDifferentiatesReviewer()
    {
        var request = new McaRequest
        {
            RequestId = 42,
            CompanyName = "Render Test Co",
            RequestNumber = "REQ-RENDER-1"
        };

        var cleanDoc = new RequestDocument
        {
            DocumentId = 101,
            RequestId = 42,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Clean_ROC.xlsx",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };

        var quarantinedDoc = new RequestDocument
        {
            DocumentId = 102,
            RequestId = 42,
            DocumentType = DocumentType.Other,
            OriginalFileName = "Suspicious_File.csv",
            UploadStatus = DocumentUploadStatus.Quarantined,
            QuarantineReason = "File checksum failure",
            UploadedDate = DateTime.UtcNow
        };

        var archiveDoc = new RequestDocument
        {
            DocumentId = 103,
            RequestId = 42,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "Filings.zip",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };

        var batch = new McaFilingBatch
        {
            BatchId = 1,
            RequestId = 42,
            SourceDocumentId = 103,
            Status = FilingBatchStatus.Completed
        };

        var vm = new RequestDetailsViewModel
        {
            Request = request,
            Documents = [cleanDoc, quarantinedDoc, archiveDoc],
            FilingBatch = batch
        };

        // 1. Render as Anonymous
        var anonHtml = await RenderDocumentsTabAsync(vm, isReviewer: false);
        // Clean doc has download button
        Assert.Contains("/Requests/42/uploaded-documents/101/download", anonHtml);
        Assert.Contains("Download", anonHtml);
        // Quarantined doc shows Unavailable badge and no active download button
        Assert.Contains("Unavailable", anonHtml);
        Assert.DoesNotContain("/Requests/42/uploaded-documents/102/download", anonHtml);
        // Archive doc has Download Archive button
        Assert.Contains("/Requests/42/uploaded-documents/103/download", anonHtml);
        Assert.Contains("Download Archive (.zip)", anonHtml);

        // 2. Render as Reviewer
        var reviewerHtml = await RenderDocumentsTabAsync(vm, isReviewer: true);
        // Reviewer gets Download Quarantined button
        Assert.Contains("/Requests/42/uploaded-documents/102/download", reviewerHtml);
        Assert.Contains("Download Quarantined", reviewerHtml);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("MCAROC.Portal not found in parent hierarchy");
    }

    private static async Task<string> RenderDocumentsTabAsync(RequestDetailsViewModel model, bool isReviewer)
    {
        var services = new ServiceCollection();
        var repoRoot = FindRepoRoot();
        var webAppDir = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis");
        var webRoot = Path.Combine(webAppDir, "wwwroot");

        var env = new TestViewHostEnvironment
        {
            ApplicationName = "MCAROC_Analysis",
            ContentRootPath = webAppDir,
            WebRootPath = webRoot,
            ContentRootFileProvider = new PhysicalFileProvider(webAppDir),
            WebRootFileProvider = new PhysicalFileProvider(webRoot)
        };
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        var diag = new DiagnosticListener("Microsoft.AspNetCore");
        services.AddSingleton<DiagnosticSource>(diag);
        services.AddSingleton<DiagnosticListener>(diag);
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        services.AddLogging();
        services.AddControllersWithViews();
        services.AddSingleton<IAuthenticationService>(new FakeAuthService(isReviewer));

        var sp = services.BuildServiceProvider();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var routeData = new RouteData();
        routeData.Values["controller"] = "Requests";
        routeData.Values["action"] = "Details";
        routeData.Routers.Add(new RouteCollection());
        var actionContext = new ActionContext(httpContext, routeData, new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_DocumentsTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_DocumentsTab", isMainPage: false);
        }

        if (!viewResult.Success)
        {
            var searched = string.Join(Environment.NewLine, viewResult.SearchedLocations ?? []);
            throw new InvalidOperationException($"Could not find view '{viewPath}'. Searched locations:{Environment.NewLine}{searched}");
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<RequestDetailsViewModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        var tempData = new TempDataDictionary(actionContext.HttpContext, tempDataProvider);
        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            tempData,
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private sealed class TestViewHostEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeEnv(string contentRoot) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeAuthService(bool isReviewer) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            if (isReviewer && scheme == "InternalReviewer")
            {
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "InternalReviewer")], "InternalReviewer");
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, "InternalReviewer");
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
