using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

public class StoragePathResolverTests : IDisposable
{
    private readonly string _tempRoot;

    public StoragePathResolverTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "StoragePathResolverTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true); } catch { }
    }

    private class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
    }

    [Fact]
    public void StoragePathResolver_DefaultDataRoot_UsesAppDataUnderContentRoot()
    {
        var env = new FakeEnv(_tempRoot);
        var resolver = new StoragePathResolver(env, null);

        var expectedRoot = Path.GetFullPath(Path.Combine(_tempRoot, "App_Data"));
        Assert.Equal(expectedRoot, resolver.GetDataRoot());
    }

    [Fact]
    public void StoragePathResolver_ConfiguredDataRoot_AbsoluteAndRelative()
    {
        var env = new FakeEnv(_tempRoot);

        // Absolute configured path
        var customAbs = Path.Combine(Path.GetTempPath(), "CustomStorageAbs_" + Guid.NewGuid().ToString("N"));
        var optionsAbs = Options.Create(new StorageOptions { Root = customAbs });
        var resolverAbs = new StoragePathResolver(env, optionsAbs);
        Assert.Equal(Path.GetFullPath(customAbs), resolverAbs.GetDataRoot());

        // Relative configured path
        var optionsRel = Options.Create(new StorageOptions { Root = "CustomRelativeData" });
        var resolverRel = new StoragePathResolver(env, optionsRel);
        var expectedRel = Path.GetFullPath(Path.Combine(_tempRoot, "CustomRelativeData"));
        Assert.Equal(expectedRel, resolverRel.GetDataRoot());
    }

    [Fact]
    public void StoragePathResolver_ToRelativePath_ConvertsAppDataPaths()
    {
        var env = new FakeEnv(_tempRoot);
        var resolver = new StoragePathResolver(env, null);

        // Path containing App_Data in Windows style
        var winPath = @"C:\OldDeploy\MCAROC.Portal\MCAROC_Analysis\App_Data\Requests\25\litigation-orders\6574-order.pdf";
        var relWin = resolver.ToRelativePath(winPath);
        Assert.Equal("Requests/25/litigation-orders/6574-order.pdf", relWin);

        // Path containing App_Data in Linux style
        var linuxPath = "/var/app/deploy-123/App_Data/Uploads/10/original/55.xlsx";
        var relLinux = resolver.ToRelativePath(linuxPath);
        Assert.Equal("Uploads/10/original/55.xlsx", relLinux);

        // Path starting directly with App_Data
        Assert.Equal("Requests/5/test.pdf", resolver.ToRelativePath("App_Data/Requests/5/test.pdf"));
        Assert.Equal("Requests/5/test.pdf", resolver.ToRelativePath(@"App_Data\Requests\5\test.pdf"));

        // Already relative path
        Assert.Equal("Requests/25/litigation-orders/order.pdf", resolver.ToRelativePath("Requests/25/litigation-orders/order.pdf"));
        Assert.Equal("Requests/25/litigation-orders/order.pdf", resolver.ToRelativePath(@"\Requests\25\litigation-orders\order.pdf"));

        // Path under active data root
        var underRoot = Path.Combine(resolver.GetDataRoot(), "PreLoginReports", "job1.docx");
        Assert.Equal("PreLoginReports/job1.docx", resolver.ToRelativePath(underRoot));

        // External path without App_Data remains unchanged
        var external = @"D:\SeparateVolume\Shared\Archive.zip";
        Assert.Equal(external, resolver.ToRelativePath(external));
    }

    [Fact]
    public void StoragePathResolver_ResolveFullPath_ResolvesRelativeAndRemapsLegacy()
    {
        var env = new FakeEnv(_tempRoot);
        var resolver = new StoragePathResolver(env, null);

        // Relative path resolves under active DataRoot
        var resolved = resolver.ResolveFullPath("Requests/25/litigation-orders/order.pdf");
        var expected = Path.GetFullPath(Path.Combine(resolver.GetDataRoot(), "Requests", "25", "litigation-orders", "order.pdf"));
        Assert.Equal(expected, resolved);

        // Legacy absolute path containing App_Data remaps to active DataRoot
        var legacyOldWorktree = @"E:\OldWorktree\MCAROC.Portal\MCAROC_Analysis\App_Data\Requests\25\litigation-orders\order.pdf";
        var remapped = resolver.ResolveFullPath(legacyOldWorktree);
        Assert.Equal(expected, remapped);

        // External path remains absolute
        var external = @"D:\External\data.zip";
        Assert.Equal(Path.GetFullPath(external), resolver.ResolveFullPath(external));
    }

    [Fact]
    public void StoragePathResolver_IsContained_PreventsTraversal()
    {
        var env = new FakeEnv(_tempRoot);
        var resolver = new StoragePathResolver(env, null);
        var expectedDir = resolver.GetLitigationOrdersDir(25);

        // Valid inside path
        var validFile = Path.Combine(expectedDir, "order.pdf");
        Assert.True(resolver.IsContained(validFile, expectedDir));

        // Subdirectory inside expected
        var validSub = Path.Combine(expectedDir, "_zip", "archive.zip");
        Assert.True(resolver.IsContained(validSub, expectedDir));

        // Path traversal escaping directory
        var traversal = Path.Combine(expectedDir, "..", "..", "Uploads", "evil.exe");
        Assert.False(resolver.IsContained(traversal, expectedDir));

        // Sibling folder traversal prefix attack
        var sibling = expectedDir.TrimEnd(Path.DirectorySeparatorChar) + "_extra" + Path.DirectorySeparatorChar + "evil.pdf";
        Assert.False(resolver.IsContained(sibling, expectedDir));
    }

    [Fact]
    public async Task StoragePathResolver_AppFolderRelocation_ResolvesFilesAndPassesDownloadCheck()
    {
        // 1. Old app folder with old App_Data
        var oldAppFolder = Path.Combine(_tempRoot, "App_Old");
        var oldAppData = Path.Combine(oldAppFolder, "App_Data");
        var oldEnv = new FakeEnv(oldAppFolder);
        var oldResolver = new StoragePathResolver(oldEnv, null);

        // 2. New app folder where app and App_Data were relocated
        var newAppFolder = Path.Combine(_tempRoot, "App_New");
        var newAppData = Path.Combine(newAppFolder, "App_Data");
        var newEnv = new FakeEnv(newAppFolder);
        var newResolver = new StoragePathResolver(newEnv, null);

        // Create the physical order PDF under New App_Data
        var newOrdersDir = newResolver.GetLitigationOrdersDir(25);
        Directory.CreateDirectory(newOrdersDir);
        var physicalPdf = Path.Combine(newOrdersDir, "6574-leaseToken.pdf");
        await File.WriteAllBytesAsync(physicalPdf, [0x25, 0x50, 0x44, 0x46, 0x2D]); // "%PDF-"

        // Database record has the legacy absolute path pointing to Old App_Data
        var legacyDbPath = Path.Combine(oldResolver.GetLitigationOrdersDir(25), "6574-leaseToken.pdf");

        // When reader runs in New App:
        var resolvedFullPath = newResolver.ResolveFullPath(legacyDbPath);

        // Assert that the legacy path was remapped to the new active folder
        Assert.Equal(Path.GetFullPath(physicalPdf), resolvedFullPath);
        Assert.True(File.Exists(resolvedFullPath), "Physical file must exist at resolved path");

        // Containment check against New App's expected directory passes
        var expectedDir = newResolver.GetLitigationOrdersDir(25);
        Assert.True(newResolver.IsContained(resolvedFullPath, expectedDir));
    }

    [Fact]
    public async Task StoragePathBackfillService_RewritesLegacyAbsolutePaths_AndPreservesExternal()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("StorageBackfill");
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(isolated.ConnectionString).Options);
        var env = new FakeEnv(_tempRoot);
        var resolver = new StoragePathResolver(env, null);
        var logger = NullLogger<StoragePathBackfillService>.Instance;
        var backfillService = new StoragePathBackfillService(db, resolver, logger);

        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10],
            ClientName = "Storage Backfill Test",
            CreatedDate = DateTime.UtcNow
        };
        var req = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Test Company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}",
            CreatedDate = DateTime.UtcNow
        };
        db.AddRange(client, req);
        await db.SaveChangesAsync();

        var litCase = new LitigationCase
        {
            RequestId = req.RequestId,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(litCase);
        await db.SaveChangesAsync();

        var litOrder = new LitigationCaseOrder
        {
            LitigationCaseId = litCase.LitigationCaseId,
            CreatedUtc = DateTime.UtcNow
        };
        db.LitigationCaseOrders.Add(litOrder);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = req.RequestId,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            RequestId = req.RequestId,
            BatchId = batch.BatchId
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        // Seed 1: LitigationOrderDocument with legacy App_Data path
        var lodLegacy = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litOrder.LitigationCaseOrderId,
            Version = 1,
            Status = LitigationOrderDocumentStatus.Downloaded,
            StoragePath = @"E:\MCAROC_wt_e2eround\MCAROC.Portal\MCAROC_Analysis\App_Data\Requests\25\litigation-orders\order1.pdf"
        };
        // Seed 2: LitigationOrderDocument already relative
        var lodRel = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litOrder.LitigationCaseOrderId,
            Version = 2,
            Status = LitigationOrderDocumentStatus.Downloaded,
            StoragePath = "Requests/25/litigation-orders/order2.pdf"
        };
        // Seed 3: LitigationOrderDocument external outside App_Data
        var lodExt = new LitigationOrderDocument
        {
            LitigationCaseOrderId = litOrder.LitigationCaseOrderId,
            Version = 3,
            Status = LitigationOrderDocumentStatus.Downloaded,
            StoragePath = @"D:\Volumes\Archive\order3.pdf"
        };
        db.LitigationOrderDocuments.AddRange(lodLegacy, lodRel, lodExt);

        // Seed 4: McaFilingDocument with legacy paths
        var mfd = new McaFilingDocument
        {
            FilingId = filing.FilingId,
            BatchId = batch.BatchId,
            RequestId = req.RequestId,
            StoragePath = @"C:\Deploy\App_Data\Requests\1\mca-filings\1\1\documents\doc1.pdf",
            ExtractedTextPath = @"C:\Deploy\App_Data\Requests\1\mca-filings\1\1\text\doc1.txt"
        };
        db.McaFilingDocuments.Add(mfd);

        // Seed 5: PreLoginReportJob with legacy path
        var plr = new PreLoginReportJob
        {
            BatchId = Guid.NewGuid(),
            ReportStoragePath = @"C:\Old\App_Data\PreLoginReports\job1-Report.docx"
        };
        db.PreLoginReportJobs.Add(plr);

        // Seed 6: RequestDocument with legacy path
        var rd = new RequestDocument
        {
            RequestId = req.RequestId,
            StoragePath = @"D:\App\App_Data\Uploads\1\original\1.xlsx"
        };
        db.RequestDocuments.Add(rd);
        await db.SaveChangesAsync();

        // Seed 7: RequestDocumentDerivative with legacy path
        var rdd = new RequestDocumentDerivative
        {
            DocumentId = rd.DocumentId,
            RequestId = req.RequestId,
            StoragePath = @"D:\App\App_Data\Uploads\1\derivatives\1_sanitized.xlsx"
        };
        db.RequestDocumentDerivatives.Add(rdd);

        // Seed 8: LargeArchiveUploadSession with legacy paths
        var laus = new LargeArchiveUploadSession
        {
            SessionId = Guid.NewGuid(),
            RequestId = req.RequestId,
            StagingFilePath = @"C:\App_Data\Requests\1\mca-filings\staging\session\archive.part",
            DestinationStoragePath = @"C:\App_Data\Uploads\1\original\archive.zip"
        };
        db.LargeArchiveUploadSessions.Add(laus);

        await db.SaveChangesAsync();

        // Run backfill
        var report = await backfillService.RunAsync();

        Assert.Equal(8, report.RowsScanned);
        Assert.Equal(6, report.RowsRewritten);
        Assert.Single(report.UnrewrittenPaths);
        Assert.Equal("LitigationOrderDocuments", report.UnrewrittenPaths[0].EntityName);
        Assert.Equal(@"D:\Volumes\Archive\order3.pdf", report.UnrewrittenPaths[0].Path);

        // Check in db context
        var updatedLod1 = await db.LitigationOrderDocuments.FindAsync(lodLegacy.LitigationOrderDocumentId);
        Assert.Equal("Requests/25/litigation-orders/order1.pdf", updatedLod1!.StoragePath);

        var updatedLod2 = await db.LitigationOrderDocuments.FindAsync(lodRel.LitigationOrderDocumentId);
        Assert.Equal("Requests/25/litigation-orders/order2.pdf", updatedLod2!.StoragePath);

        var updatedLod3 = await db.LitigationOrderDocuments.FindAsync(lodExt.LitigationOrderDocumentId);
        Assert.Equal(@"D:\Volumes\Archive\order3.pdf", updatedLod3!.StoragePath);

        var updatedMfd = await db.McaFilingDocuments.FindAsync(mfd.FilingDocumentId);
        Assert.Equal("Requests/1/mca-filings/1/1/documents/doc1.pdf", updatedMfd!.StoragePath);
        Assert.Equal("Requests/1/mca-filings/1/1/text/doc1.txt", updatedMfd.ExtractedTextPath);

        var updatedPlr = await db.PreLoginReportJobs.FindAsync(plr.PreLoginReportJobId);
        Assert.Equal("PreLoginReports/job1-Report.docx", updatedPlr!.ReportStoragePath);

        var updatedRd = await db.RequestDocuments.FindAsync(rd.DocumentId);
        Assert.Equal("Uploads/1/original/1.xlsx", updatedRd!.StoragePath);

        var updatedRdd = await db.RequestDocumentDerivatives.FindAsync(rdd.DerivativeId);
        Assert.Equal("Uploads/1/derivatives/1_sanitized.xlsx", updatedRdd!.StoragePath);

        var updatedLaus = await db.LargeArchiveUploadSessions.FindAsync(laus.SessionId);
        Assert.Equal("Requests/1/mca-filings/staging/session/archive.part", updatedLaus!.StagingFilePath);
        Assert.Equal("Uploads/1/original/archive.zip", updatedLaus.DestinationStoragePath);

        // Idempotency: second run rewrites 0 rows
        var secondReport = await backfillService.RunAsync();
        Assert.Equal(0, secondReport.RowsRewritten);
    }
}

