using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NPOI.XSSF.UserModel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class WorkbookDerivativeServiceTests : IAsyncLifetime, IDisposable
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"mcaroc_deriv_tests_{Guid.NewGuid():N}");

    public WorkbookDerivativeServiceTests()
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

    private WorkbookDerivativeService CreateService(AppDbContext db)
    {
        var env = new FakeEnv(_tempDir);
        var fileVal = new FileValidationService(new ExcelSheetReader());
        return new WorkbookDerivativeService(db, env, fileVal, NullLogger<WorkbookDerivativeService>.Instance);
    }

    private static string CreateSyntheticWorkbook(string filePath, string label = "Printed at")
    {
        using (var wb = new XSSFWorkbook())
        {
            var sheet = wb.CreateSheet("About the Company");
            var r0 = sheet.CreateRow(0);
            r0.CreateCell(0).SetCellValue("Legal Name");
            r0.CreateCell(1).SetCellValue("Sample Co");

            var r1 = sheet.CreateRow(1);
            r1.CreateCell(0).SetCellValue(label);
            r1.CreateCell(1).SetCellValue("15 Sep 2026");

            var r2 = sheet.CreateRow(2);
            r2.CreateCell(0).SetCellValue("Authorised Capital (Crore)");
            r2.CreateCell(1).SetCellValue("100");

            using var fs = File.Create(filePath);
            wb.Write(fs);
        }

        using var readStream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(readStream));
    }

    [Fact]
    public async Task GetOrCreateSanitizedDerivativeAsync_CreatesReadyDerivative_PreservesRawEvidence()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Derivative Test Co",
            RequestNumber = $"DTV-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);
        var originalLength = new FileInfo(rawPath).Length;

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Report.xlsx",
            StoredFileName = "sample.xlsx",
            StoragePath = rawPath,
            FileSize = originalLength,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var derivative = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(derivative);
        Assert.Equal(DocumentDerivativeStatus.Ready, derivative.Status);
        Assert.Equal(doc.DocumentId, derivative.DocumentId);
        Assert.Equal(request.RequestId, derivative.RequestId);
        Assert.Equal(WorkbookDerivativeService.CurrentSanitizerVersion, derivative.SanitizerVersion);
        Assert.Equal(originalHash, derivative.RawFileHash);
        Assert.True(File.Exists(derivative.StoragePath));
        Assert.NotEqual(rawPath, derivative.StoragePath);

        // RAW FILE MUST BE 100% UNCHANGED
        Assert.True(File.Exists(rawPath));
        using (var fs = File.OpenRead(rawPath))
        {
            var rawCurrentHash = Convert.ToHexString(SHA256.HashData(fs));
            Assert.Equal(originalHash, rawCurrentHash);
        }
        Assert.Equal(originalLength, new FileInfo(rawPath).Length);

        // Derivative must be sanitized and openable
        var reader = new ExcelSheetReader();
        var sheets = reader.ReadWorkbook(derivative.StoragePath);
        var aboutSheet = sheets.First(s => s.Name == "About the Company");
        var labels = aboutSheet.Rows.Select(r => r.Count > 0 ? r[0]?.ToString()?.Trim() : null).ToList();
        Assert.DoesNotContain(labels, l => l == "Printed at");
        Assert.Contains(labels, l => l == "Legal Name");

        // Idempotency: second call returns the existing record immediately without modifying it
        var second = await service.GetOrCreateSanitizedDerivativeAsync(doc);
        Assert.NotNull(second);
        Assert.Equal(derivative.DerivativeId, second.DerivativeId);
        Assert.Equal(derivative.FileHash, second.FileHash);
    }

    [Fact]
    public async Task GetOrCreateSanitizedDerivativeAsync_ExpiredPendingLease_TakesOverAndGenerates()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Takeover Test Co",
            RequestNumber = $"TOV-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample_takeover.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Takeover.xlsx",
            StoredFileName = "sample_takeover.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        // Seed an expired pending derivative lease from a crashed worker
        var oldLeaseToken = Guid.NewGuid();
        var expiredDerivative = new RequestDocumentDerivative
        {
            DocumentId = doc.DocumentId,
            RequestId = request.RequestId,
            DerivativeType = DocumentDerivativeType.SanitizedExcel,
            SanitizerVersion = WorkbookDerivativeService.CurrentSanitizerVersion,
            RawFileHash = originalHash,
            Status = DocumentDerivativeStatus.Pending,
            LeaseToken = oldLeaseToken,
            LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5), // expired 5 mins ago
            CreatedUtc = DateTime.UtcNow.AddMinutes(-10),
            UpdatedUtc = DateTime.UtcNow.AddMinutes(-10),
            StoragePath = string.Empty,
            FileHash = string.Empty
        };
        db.RequestDocumentDerivatives.Add(expiredDerivative);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(result);
        Assert.Equal(expiredDerivative.DerivativeId, result.DerivativeId);
        Assert.Equal(DocumentDerivativeStatus.Ready, result.Status);
        Assert.NotEqual(oldLeaseToken, result.LeaseToken);
        Assert.True(File.Exists(result.StoragePath));
    }

    [Fact]
    public async Task GetOrCreateSanitizedDerivativeAsync_MissingSourceFile_SetsFailedStatus()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Missing File Test Co",
            RequestNumber = $"MSF-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var nonExistentPath = Path.Combine(_tempDir, "does_not_exist.xlsx");
        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "does_not_exist.xlsx",
            StoredFileName = "does_not_exist.xlsx",
            StoragePath = nonExistentPath,
            FileSize = 1000,
            FileHash = "abc123",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        var result = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(result);
        Assert.Equal(DocumentDerivativeStatus.Failed, result.Status);
        Assert.Contains("not found", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
