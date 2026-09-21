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
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
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
    public async Task LateWorker_WithExpiredLease_CannotPublish_EvenWithoutTakeover()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Late Worker Test Co",
            RequestNumber = $"LATE-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample_late.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Late.xlsx",
            StoredFileName = "sample_late.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service = CreateService(db);

        string? generatedFilePath = null;
        // Hook simulates Worker 1 being paused/delayed until after its lease has expired
        service.PreCasCompletionHook = async (derivId, token) =>
        {
            await using var hookDb = CreateContext();
            var deriv = await hookDb.RequestDocumentDerivatives.FindAsync(derivId);
            Assert.NotNull(deriv);
            // Expire the lease before CAS runs
            deriv.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            await hookDb.SaveChangesAsync();

            var derivativesDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "derivatives");
            var expectedFile = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{token:N}.xlsx");
            Assert.True(File.Exists(expectedFile), "Worker 1 must have produced its generation file before CAS.");
            generatedFilePath = expectedFile;
        };

        var result = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(result);
        Assert.NotEqual(DocumentDerivativeStatus.Ready, result.Status);

        // Generation file must be cleaned up by the late worker
        Assert.NotNull(generatedFilePath);
        Assert.False(File.Exists(generatedFilePath), "Late worker must delete its own unpromoted generation file upon lease expiration.");
    }

    [Fact]
    public async Task LateWorker_WithExpiredLease_ValidationFailure_CannotMarkFailed_PreservesPendingState()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Late Validation Test Co",
            RequestNumber = $"LVAL-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample_late_val.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Late_Val.xlsx",
            StoredFileName = "sample_late_val.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        string? generatedFilePath = null;

        // Seam: before validation runs, corrupt the generation file so ValidateOpens fails,
        // and expire the lease so the worker is late.
        service.PreValidationHook = async (derivId, token) =>
        {
            var derivativesDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "derivatives");
            generatedFilePath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{token:N}.xlsx");
            Assert.True(File.Exists(generatedFilePath), "Worker must have produced generation file before validation.");

            // Corrupt file so ValidateOpens returns false
            await File.WriteAllTextAsync(generatedFilePath, "CORRUPTED_NOT_A_VALID_WORKBOOK");

            // Expire the lease in database
            await using var hookDb = CreateContext();
            var deriv = await hookDb.RequestDocumentDerivatives.FindAsync(derivId);
            Assert.NotNull(deriv);
            deriv.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            await hookDb.SaveChangesAsync();
        };

        var result = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(result);
        // Fencing prevents late worker from mutating status to Failed!
        Assert.Equal(DocumentDerivativeStatus.Pending, result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.LeaseExpiresUtc);
        Assert.True(result.LeaseExpiresUtc < DateTime.UtcNow);

        // Generation file must be cleaned up
        Assert.NotNull(generatedFilePath);
        Assert.False(File.Exists(generatedFilePath), "Corrupt generation file must be deleted.");

        // A new healthy worker can now take over and succeed
        service.PreValidationHook = null;
        var recoveryResult = await service.GetOrCreateSanitizedDerivativeAsync(doc);
        Assert.NotNull(recoveryResult);
        Assert.Equal(DocumentDerivativeStatus.Ready, recoveryResult.Status);
        Assert.True(File.Exists(recoveryResult.StoragePath));
    }

    [Fact]
    public async Task LateWorker_WithExpiredLease_Exception_CannotMarkFailed_PreservesPendingState()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Late Exception Test Co",
            RequestNumber = $"LEXC-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample_late_exc.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Late_Exc.xlsx",
            StoredFileName = "sample_late_exc.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service = CreateService(db);
        string? generatedFilePath = null;

        // Seam: simulate an unexpected exception occurring after lease expiration
        service.PreValidationHook = async (derivId, token) =>
        {
            var derivativesDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "derivatives");
            generatedFilePath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{token:N}.xlsx");

            // Expire the lease in database
            await using var hookDb = CreateContext();
            var deriv = await hookDb.RequestDocumentDerivatives.FindAsync(derivId);
            Assert.NotNull(deriv);
            deriv.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            await hookDb.SaveChangesAsync();

            throw new InvalidOperationException("Simulated late processing crash.");
        };

        var result = await service.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(result);
        // Fencing prevents late worker from mutating status to Failed!
        Assert.Equal(DocumentDerivativeStatus.Pending, result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(result.LeaseExpiresUtc);
        Assert.True(result.LeaseExpiresUtc < DateTime.UtcNow);

        // Generation file must be cleaned up
        if (generatedFilePath != null)
        {
            Assert.False(File.Exists(generatedFilePath), "Generation file must be cleaned up by catch block.");
        }

        // A new healthy worker can now take over and succeed
        service.PreValidationHook = null;
        var recoveryResult = await service.GetOrCreateSanitizedDerivativeAsync(doc);
        Assert.NotNull(recoveryResult);
        Assert.Equal(DocumentDerivativeStatus.Ready, recoveryResult.Status);
        Assert.True(File.Exists(recoveryResult.StoragePath));
    }

    [Fact]
    public async Task InterleavedTakeover_WinnerFileAndHashRemainAuthoritative()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Interleaved Test Co",
            RequestNumber = $"INT-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var uploadDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "original");
        Directory.CreateDirectory(uploadDir);
        var rawPath = Path.Combine(uploadDir, "sample_interleaved.xlsx");
        var originalHash = CreateSyntheticWorkbook(rawPath);

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Interleaved.xlsx",
            StoredFileName = "sample_interleaved.xlsx",
            StoragePath = rawPath,
            FileSize = new FileInfo(rawPath).Length,
            FileHash = originalHash,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var service1 = CreateService(db);
        var service2 = CreateService(db);

        string? worker1FilePath = null;
        string? worker2FilePath = null;
        string? worker2Hash = null;

        // Seam: Pause Worker 1 right before CAS, force lease takeover and completion by Worker 2
        service1.PreCasCompletionHook = async (derivId, worker1Token) =>
        {
            var derivativesDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "derivatives");
            worker1FilePath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{worker1Token:N}.xlsx");
            Assert.True(File.Exists(worker1FilePath), "Worker 1 should have produced its generation file.");

            // Expire Worker 1's lease in the database so Worker 2 can claim it
            await using var hookDb = CreateContext();
            var deriv = await hookDb.RequestDocumentDerivatives.FindAsync(derivId);
            Assert.NotNull(deriv);
            deriv.LeaseExpiresUtc = DateTime.UtcNow.AddMinutes(-5);
            await hookDb.SaveChangesAsync();

            // Worker 2 takes over and finishes completely
            var worker2Result = await service2.GetOrCreateSanitizedDerivativeAsync(doc);
            Assert.NotNull(worker2Result);
            Assert.Equal(DocumentDerivativeStatus.Ready, worker2Result.Status);
            worker2FilePath = worker2Result.StoragePath;
            worker2Hash = worker2Result.FileHash;
            Assert.True(File.Exists(worker2FilePath), "Worker 2's file must exist.");
        };

        // Worker 1 runs and attempts its CAS after Worker 2 has already won and promoted
        var finalResult = await service1.GetOrCreateSanitizedDerivativeAsync(doc);

        Assert.NotNull(finalResult);
        Assert.Equal(DocumentDerivativeStatus.Ready, finalResult.Status);

        // Worker 2's results must remain authoritative!
        Assert.Equal(worker2FilePath, finalResult.StoragePath);
        Assert.Equal(worker2Hash, finalResult.FileHash);

        // Worker 1's generation file must have been deleted/cleaned up
        Assert.NotNull(worker1FilePath);
        Assert.False(File.Exists(worker1FilePath), "Worker 1's stale generation file must be deleted.");

        // Worker 2's physical file must be intact on disk
        Assert.NotNull(worker2FilePath);
        Assert.True(File.Exists(worker2FilePath), "Worker 2's authoritative file must still exist.");
        using (var fs = File.OpenRead(worker2FilePath))
        {
            var currentHash = Convert.ToHexString(SHA256.HashData(fs));
            Assert.Equal(worker2Hash, currentHash);
        }
    }

    [Fact]
    public async Task OrphanCleanup_RemovesUnreferencedStaleGenerationFiles()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Orphan Cleanup Co",
            RequestNumber = $"ORPH-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var doc = new RequestDocument
        {
            RequestId = request.RequestId,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "Sample_Report.xlsx",
            StoredFileName = "sample.xlsx",
            StoragePath = "dummy.xlsx",
            FileSize = 123,
            FileHash = "hash99",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var derivativesDir = Path.Combine(_tempDir, "App_Data", "Uploads", request.RequestId.ToString(), "derivatives");
        Directory.CreateDirectory(derivativesDir);

        // Active file
        var activePath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.active.xlsx");
        File.WriteAllText(activePath, "active content");

        var deriv = new RequestDocumentDerivative
        {
            DocumentId = doc.DocumentId,
            RequestId = request.RequestId,
            DerivativeType = DocumentDerivativeType.SanitizedExcel,
            SanitizerVersion = 1,
            RawFileHash = "hash99",
            Status = DocumentDerivativeStatus.Ready,
            StoragePath = activePath,
            FileHash = "hash_active",
            CreatedUtc = DateTime.UtcNow,
            UpdatedUtc = DateTime.UtcNow
        };
        db.RequestDocumentDerivatives.Add(deriv);
        await db.SaveChangesAsync();

        // Stale orphan file from a crashed worker
        var orphanPath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.crashed123.xlsx");
        File.WriteAllText(orphanPath, "crashed content");
        File.SetLastWriteTimeUtc(orphanPath, DateTime.UtcNow.AddHours(-2));

        var service = CreateService(db);
        var deletedCount = await service.CleanupOrphanGenerationsAsync(request.RequestId, TimeSpan.FromMinutes(30));

        Assert.Equal(1, deletedCount);
        Assert.False(File.Exists(orphanPath), "Stale orphan file should have been deleted.");
        Assert.True(File.Exists(activePath), "Active published file must be preserved.");
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

