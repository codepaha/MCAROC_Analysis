using System.IO.Compression;
using System.Security.Claims;
using System.Security.Cryptography;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Storage;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NPOI.XSSF.UserModel;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class StoragePathIntegrationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        StoragePathHelper.Reset();
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
    }

    private sealed class FakeAuthService : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private static void CreateRealSyntheticWorkbook(string filePath, string cin, string legalName)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var wb = new XSSFWorkbook();
        var s1 = wb.CreateSheet("About the Company");
        var r0 = s1.CreateRow(0);
        r0.CreateCell(0).SetCellValue("Printed at");
        r0.CreateCell(1).SetCellValue("10 Mar, 2026 10:00 Hours");

        var r1 = s1.CreateRow(1);
        r1.CreateCell(0).SetCellValue("Legal Name");
        r1.CreateCell(1).SetCellValue(legalName);

        var r2 = s1.CreateRow(2);
        r2.CreateCell(0).SetCellValue("CIN");
        r2.CreateCell(1).SetCellValue(cin);

        var r3 = s1.CreateRow(3);
        r3.CreateCell(0).SetCellValue("PAN");
        r3.CreateCell(1).SetCellValue("AAAAA0000A");

        var r4 = s1.CreateRow(4);
        r4.CreateCell(0).SetCellValue("Company Status");
        r4.CreateCell(1).SetCellValue("Active");

        var s2 = wb.CreateSheet("Directors");
        var d0 = s2.CreateRow(0);
        d0.CreateCell(0).SetCellValue("NAME");
        d0.CreateCell(1).SetCellValue("DIN");
        d0.CreateCell(2).SetCellValue("PRESENT DESIGNATION");
        d0.CreateCell(3).SetCellValue("PRESENT DESIGNATION APPOINTMENT DATE");
        d0.CreateCell(4).SetCellValue("ORIGINAL APPOINTMENT DATE");
        d0.CreateCell(5).SetCellValue("DATE OF CESSATION");
        d0.CreateCell(6).SetCellValue("FLAGS");

        var d1 = s2.CreateRow(1);
        d1.CreateCell(0).SetCellValue("TEST DIRECTOR");
        d1.CreateCell(1).SetCellValue(12345678.0);
        d1.CreateCell(2).SetCellValue("Director");
        d1.CreateCell(3).SetCellValue("1 Jan, 2020");
        d1.CreateCell(4).SetCellValue("1 Jan, 2020");
        d1.CreateCell(5).SetCellValue("-");
        d1.CreateCell(6).SetCellValue("-");

        using var fs = File.Create(filePath);
        wb.Write(fs);
    }

    private static string CreateValidZip(string path, string entryName = "doc.txt", string content = "integration test payload")
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        var bytes = File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private const string Lifex2014CertificateText = """
        --- Page 1 (native) ---
        GOVERNMENT OF INDIA
        MINISTRY OF CORPORATE AFFAIRS
        Office of the Central Processing Centre
        Certificate of Incorporation pursuant to change of name [Pursuant to rule 29 of the Companies (Incorporation) Rules, 2014]
        Corporate Identification Number (CIN): U51909DL2019PTC355815
        I hereby certify that the name of the company has been changed from KE HEALTHCARE PRIVATE LIMITED to LIFEX SCIENCES PRIVATE LIMITED with effect from the date of this certificate and that the company is Company limited by shares.
        Company was originally incorporated with the name CINQUE AYURVEDA SCIENCES PRIVATE LIMITED
        Given under my hand at ROC, CPC this  ELEVENTH day of APRIL  TWO THOUSAND TWENTY FIVE
        Assistant Registrar of Companies/ Deputy Registrar of Companies/ Registrar of Companies
        Central Processing Centre
        """;

    [Fact]
    public async Task Upload_To_RealWorkbookIngestion_WithDistinctWorkingDirectory_SucceedsAndDoesNotEscapeToCwd()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("IngestRelWorkDir");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);

        var customDataRoot = CreateTempDir("CustomDataRoot");
        var appContentRoot = CreateTempDir("AppContentRoot");
        var env = new FakeEnv(appContentRoot);
        var resolver = new StoragePathResolver(env, Options.Create(new StorageOptions { Root = customDataRoot }));
        StoragePathHelper.Initialize(resolver);

        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10],
            ClientName = "Test Client",
            CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "TEST COMPANY PRIVATE LIMITED",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            Cin = "U00000TEST0000000001",
            RequestStatus = RequestStatus.DocumentsUploaded,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var relDocPath = $"Uploads/{request.RequestId}/original/{Guid.NewGuid():N}.xlsx";
        var physicalPath = resolver.ResolveFullPath(relDocPath);
        CreateRealSyntheticWorkbook(physicalPath, request.Cin, request.CompanyName);

        var rocDoc = new RequestDocument
        {
            Request = request,
            DocumentType = DocumentType.McaRocReport,
            OriginalFileName = "roc.xlsx",
            StoredFileName = Path.GetFileName(physicalPath),
            StoragePath = relDocPath, // relative path stored in database!
            FileHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(physicalPath))),
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(rocDoc);
        await db.SaveChangesAsync();

        // Use real ExcelSheetReader reading the relative storage path via IngestionOrchestrator
        var realSheetReader = new ExcelSheetReader();
        var orchestrator = new IngestionOrchestrator(db, realSheetReader, NullLogger<IngestionOrchestrator>.Instance, pathResolver: resolver);

        var run = await orchestrator.RunAsync(request.RequestId, rocDoc.DocumentId, chargeDocumentId: null);

        Assert.Equal(IngestionRunStatus.CompletedClean, run.Status);
        Assert.Equal(1, run.RunNumber);
        Assert.Equal(new DateTime(2026, 3, 10, 10, 0, 0), run.SourceSnapshotDate);

        // Verify company profile was persisted in DB
        var profile = await db.CompanyProfiles.FirstOrDefaultAsync(p => p.RequestId == request.RequestId);
        Assert.NotNull(profile);
        Assert.Equal("TEST COMPANY PRIVATE LIMITED", profile.CompanyName);

        // Assert nothing escaped into process current working directory
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), relDocPath)));
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Uploads", request.RequestId.ToString())));
    }

    [Fact]
    public async Task ResumableUpload_To_Finalization_WithDistinctWorkingDirectory_MovesToCustomDataRootAndDoesNotEscapeToCwd()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("FinalizeRelWorkDir");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);

        var customDataRoot = CreateTempDir("CustomDataRootFinalize");
        var appContentRoot = CreateTempDir("AppContentRootFinalize");
        var env = new FakeEnv(appContentRoot);
        var resolver = new StoragePathResolver(env, Options.Create(new StorageOptions { Root = customDataRoot }));
        StoragePathHelper.Initialize(resolver);

        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10],
            ClientName = "Test Client",
            CreatedDate = DateTime.UtcNow
        };
        db.Clients.Add(client);

        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Test Co",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            Cin = "U12345MH2026PTC123456",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var sessionId = Guid.NewGuid();
        var clientToken = "token_" + Guid.NewGuid().ToString("N");
        var stagingRel = $"Requests/{request.RequestId}/mca-filings/staging/{sessionId:N}/archive.part";
        var destRel = $"Uploads/{request.RequestId}/original/{sessionId:N}.zip";

        var stagingFull = resolver.ResolveFullPath(stagingRel);
        var destFull = resolver.ResolveFullPath(destRel);

        var sha = CreateValidZip(stagingFull, "doc.txt", "payload for resumable finalization");
        var fileSize = new FileInfo(stagingFull).Length;

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = request.RequestId,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash(clientToken),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fileSize,
            NextExpectedOffset = fileSize,
            ExpectedFullSha256 = sha,
            Status = LargeArchiveUploadSessionStatus.Uploading,
            StagingFilePath = stagingRel, // relative staging path
            DestinationStoragePath = destRel, // relative destination path
            CreatedUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        var opts = Options.Create(new LargeArchiveUploadOptions
        {
            MaxUncompressedSizeBytes = 10 * 1024 * 1024,
            MaxNestedTempBytes = 1 * 1024 * 1024,
            MinFreeDiskHeadroomBytes = 1 * 1024 * 1024,
            ChunkSizeBytes = 64 * 1024
        });
        var resManager = new StorageReservationManager(db, opts, NullLogger<StorageReservationManager>.Instance);
        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var queue = new FilingProcessingQueue();

        var reserveResult = await resManager.TryReserveUploadCapacityAsync(sessionId, fileSize, Path.GetDirectoryName(stagingFull)!, Path.GetDirectoryName(destFull)!);
        Assert.True(reserveResult.Success, reserveResult.Error);

        var finalizationService = new FinalizationRecoveryService(db, opts, resManager, slotService, queue, NullLogger<FinalizationRecoveryService>.Instance, resolver);

        var result = await finalizationService.CompleteSessionAsync(sessionId, clientToken);
        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.DocumentId);

        // Check destination file exists in custom data root
        Assert.True(File.Exists(destFull));
        Assert.False(File.Exists(stagingFull));

        // Check created document record has relative storage path
        var createdDoc = await db.RequestDocuments.FindAsync(result.DocumentId.Value);
        Assert.NotNull(createdDoc);
        Assert.Equal(destRel, createdDoc.StoragePath);

        // Check no files escaped to current working directory
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), stagingRel)));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), destRel)));
        Assert.False(Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "Uploads", request.RequestId.ToString())));
    }

    [Fact]
    public async Task CertificateAndBackfillExtraction_WithDistinctWorkingDirectory_ReadsTextViaResolver()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("CertBackfillWorkDir");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);

        var customDataRoot = CreateTempDir("CustomDataRootCert");
        var appContentRoot = CreateTempDir("AppContentRootCert");
        var env = new FakeEnv(appContentRoot);
        var resolver = new StoragePathResolver(env, Options.Create(new StorageOptions { Root = customDataRoot }));
        StoragePathHelper.Initialize(resolver);

        var client = new Client { ClientCode = "NC" + Guid.NewGuid().ToString("N")[..8], ClientName = "Name change", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "LIFEX SCIENCES PRIVATE LIMITED",
            RequestNumber = $"NC-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        var source = new RequestDocument
        {
            Request = request,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "f.zip",
            StoredFileName = "f.zip",
            StoragePath = "Uploads/test/f.zip",
            FileHash = "h",
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(source);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            SourceDocumentId = source.DocumentId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow,
            CompletedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            Srn = "AB1234567",
            NestedZipName = "1.zip",
            OuterCategoryFolder = "Incorporation Documents"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        // Write certificate text at relative path under custom data root
        var relText = $"Requests/{request.RequestId}/mca-filings/{batch.BatchId}/{filing.FilingId}/text/cert.txt";
        var fullText = resolver.ResolveFullPath(relText);
        Directory.CreateDirectory(Path.GetDirectoryName(fullText)!);
        await File.WriteAllTextAsync(fullText, Lifex2014CertificateText);

        var certificateDoc = new McaFilingDocument
        {
            FilingId = filing.FilingId,
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            OriginalFileName = "Certificate-of-change-of-name.pdf",
            StoragePath = $"Requests/{request.RequestId}/mca-filings/{batch.BatchId}/{filing.FilingId}/documents/cert.pdf",
            FileHash = Guid.NewGuid().ToString("N"),
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            FormType = NameChangeCertificateLoader.CertificateFormType,
            Category = FilingCategory.Constitutional,
            ClassificationMethod = "FilenameKeyword",
            UpdatedAt = DateTime.UtcNow,
            ExtractedTextPath = relText // relative path!
        };
        db.McaFilingDocuments.Add(certificateDoc);
        await db.SaveChangesAsync();

        // 1. Verify NameChangeCertificateLoader resolves relative ExtractedTextPath correctly
        using var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 256 });
        var changes = await NameChangeCertificateLoader.LoadAsync(db, request.RequestId, CancellationToken.None, cache);
        var change = Assert.Single(changes);
        Assert.Equal(certificateDoc.FilingDocumentId, change.FilingDocumentId);
        Assert.Equal(new DateOnly(2025, 4, 11), change.EffectiveDate);

        // 2. Verify FilingReclassifier resolves relative ExtractedTextPath correctly
        certificateDoc.ClassifierVersion = 0;
        certificateDoc.FormType = null;
        certificateDoc.Category = FilingCategory.Unclassified;
        await db.SaveChangesAsync();

        var reclassifier = new FilingReclassifier(db, NullLogger<FilingReclassifier>.Instance);
        var (seen, changedCount) = await reclassifier.ReclassifyBatchAsync(10, CancellationToken.None);
        Assert.Equal(1, seen);
        Assert.Equal(1, changedCount);

        var updatedDoc = await db.McaFilingDocuments.FindAsync(certificateDoc.FilingDocumentId);
        Assert.NotNull(updatedDoc);
        Assert.Equal(NameChangeCertificateLoader.CertificateFormType, updatedDoc.FormType);
        Assert.Equal(FilingCategory.Constitutional, updatedDoc.Category);

        // 3. Assert no file accessed in or created in current working directory
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), relText)));
    }

    [Fact]
    public async Task EndToEnd_CustomRoot_StorageOptions_ResolvesFilesStrictlyWithinConfiguredRoot_AndDoesNotEscapeToDefaultAppDataOrCwd()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("CustomRootEndToEnd");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);

        var customStorageRoot = CreateTempDir("CustomRootStrict");
        var appContentRoot = CreateTempDir("AppContentRootStrict");
        var defaultAppData = Path.Combine(appContentRoot, "App_Data");

        var env = new FakeEnv(appContentRoot);
        var storageOptions = Options.Create(new StorageOptions { Root = customStorageRoot });
        var resolver = new StoragePathResolver(env, storageOptions);
        StoragePathHelper.Initialize(resolver);

        Assert.Equal(Path.GetFullPath(customStorageRoot), resolver.GetDataRoot());

        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10],
            ClientName = "Custom Root Client",
            CreatedDate = DateTime.UtcNow
        };
        var req = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Custom Root Co",
            RequestNumber = $"CR-{Guid.NewGuid():N}",
            Cin = "U99999MH2026PTC999999",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.AddRange(client, req);
        await db.SaveChangesAsync();

        var relDocPath = $"Uploads/{req.RequestId}/original/{Guid.NewGuid():N}.pdf";
        var physicalCustomPath = resolver.ResolveFullPath(relDocPath);
        Directory.CreateDirectory(Path.GetDirectoryName(physicalCustomPath)!);
        await File.WriteAllBytesAsync(physicalCustomPath, [0x25, 0x50, 0x44, 0x46, 0x2D]); // "%PDF-"

        var doc = new RequestDocument
        {
            RequestId = req.RequestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "test.pdf",
            StoredFileName = Path.GetFileName(physicalCustomPath),
            StoragePath = relDocPath,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        // Verify physical file was written inside customStorageRoot
        Assert.True(File.Exists(physicalCustomPath));
        Assert.True(resolver.IsContained(physicalCustomPath, customStorageRoot));

        // Verify default App_Data folder does NOT contain this file and was not touched
        var escapedAppDataPath = Path.Combine(defaultAppData, relDocPath);
        Assert.False(File.Exists(escapedAppDataPath));

        // Create RequestsController with configured resolver
        var fileVal = new FileValidationService(new ExcelSheetReader());
        var derivService = new WorkbookDerivativeService(db, env, fileVal, NullLogger<WorkbookDerivativeService>.Instance, resolver);
        var dpProvider = DataProtectionProvider.Create(new DirectoryInfo(customStorageRoot));
        var tokenService = new MCAROC_Analysis.Services.Documents.TimeLimitedSignedDownloadTokenService(dpProvider);

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
            derivativeService: derivService,
            tokenService: tokenService,
            pathResolver: resolver);

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthService());
        var serviceProvider = services.BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = serviceProvider };
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "portal-user")], "ApplicationUser"));
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var actionResult = await controller.DownloadUploadedDocument(req.RequestId, doc.DocumentId, token: null, CancellationToken.None);
        var physicalResult = Assert.IsType<PhysicalFileResult>(actionResult);

        Assert.Equal(physicalCustomPath, physicalResult.FileName);
        Assert.True(resolver.IsContained(physicalResult.FileName, customStorageRoot));

        // Assert zero files escaped to cwd or default App_Data
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), relDocPath)));
        Assert.False(Directory.Exists(defaultAppData));
    }
}

