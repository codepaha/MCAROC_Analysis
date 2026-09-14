using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FilingBatchProcessorLeaseTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> EnsureTestRequestAsync(AppDbContext db)
    {
        var existing = await db.Requests.FirstOrDefaultAsync();
        if (existing != null) return existing.RequestId;

        var req = new McaRequest
        {
            RequestNumber = "REQ-" + Guid.NewGuid().ToString("N")[..8],
            CompanyName = "Test Unpack Lease Co",
            Cin = "U12345MH2026PTC999999",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        return req.RequestId;
    }

    private static string CreateValidOuterZip(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (File.Exists(path)) File.Delete(path);

        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            // Empty valid zip or non-zip entry
            var entry = zip.CreateEntry("manifest.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("archive manifest");
        }

        var bytes = File.ReadAllBytes(path);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static FilingBatchProcessor CreateProcessor(AppDbContext db, string contentRootPath, IOperationalSlotLeaseService slotLeaseService)
    {
        var queue = new FilingProcessingQueue();
        var chunkQueue = new DocumentChunkingQueue();

        return new FilingBatchProcessor(
            db,
            contentRootPath,
            null,
            null,
            queue,
            chunkQueue,
            NullLogger<FilingBatchProcessor>.Instance,
            slotLeaseService);
    }

    [Fact]
    public async Task UnpackBatchAsync_CannotBegin_WhenAnotherBatchHoldsLargeUnpackSlot()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "unpack-lease-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var zip1Path = Path.Combine(tempDir, "batch1.zip");
        var zip2Path = Path.Combine(tempDir, "batch2.zip");
        var sha1 = CreateValidOuterZip(zip1Path);
        var sha2 = CreateValidOuterZip(zip2Path);

        var doc1 = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "batch1.zip",
            StoredFileName = "batch1.zip",
            StoragePath = zip1Path,
            FileSize = new FileInfo(zip1Path).Length,
            FileHash = sha1,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false
        };
        var doc2 = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "batch2.zip",
            StoredFileName = "batch2.zip",
            StoragePath = zip2Path,
            FileSize = new FileInfo(zip2Path).Length,
            FileHash = sha2,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false
        };
        db.RequestDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        var batch1 = new McaFilingBatch
        {
            RequestId = requestId,
            SourceDocumentId = doc1.DocumentId,
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow
        };
        var batch2 = new McaFilingBatch
        {
            RequestId = requestId,
            SourceDocumentId = doc2.DocumentId,
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.AddRange(batch1, batch2);
        await db.SaveChangesAsync();

        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var processor = CreateProcessor(db, tempDir, slotService);

        try
        {
            // 1. Batch 1 acquires LargeUnpackSlot directly
            var holder1 = batch1.BatchId.ToString();
            var lease1 = await slotService.TryAcquireSlotAsync(
                OperationalSlotLeaseService.LargeUnpackSlot,
                holder1,
                TimeSpan.FromMinutes(5));

            Assert.True(lease1.Success, lease1.Error);

            // 2. Batch 2 attempts UnpackBatchAsync while Batch 1 holds the lease
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                processor.UnpackBatchAsync(batch2.BatchId, CancellationToken.None));

            Assert.Contains("Cannot acquire 'LargeUnpack' slot lease", ex.Message);

            // 3. Prove Batch 2 did NOT begin unpack: status remains Uploaded (not Unpacking)
            var updatedBatch2 = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch2.BatchId);
            Assert.Equal(FilingBatchStatus.Uploaded, updatedBatch2.Status);

            // 4. Release Batch 1's lease
            await slotService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUnpackSlot, holder1);

            // 5. Batch 2 can now successfully begin and complete unpacking
            await processor.UnpackBatchAsync(batch2.BatchId, CancellationToken.None);

            updatedBatch2 = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch2.BatchId);
            Assert.Equal(FilingBatchStatus.Processing, updatedBatch2.Status);

            // 6. Verify LargeUnpackSlot was released upon completion
            var leaseState = await db.OperationalSlotLeases.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot);
            Assert.True(leaseState == null || leaseState.ActiveHolderId == null);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.BatchId == batch1.BatchId || b.BatchId == batch2.BatchId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.DocumentId == doc1.DocumentId || d.DocumentId == doc2.DocumentId).ExecuteDeleteAsync();
            await db.OperationalSlotLeases.Where(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task UnpackBatchAsync_ReleasesSlot_OnFailureTerminalPath()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "unpack-fail-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Document points to non-existent file to trigger unpack failure
        var nonExistentPath = Path.Combine(tempDir, "missing.zip");
        var doc = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "missing.zip",
            StoredFileName = "missing.zip",
            StoragePath = nonExistentPath,
            FileSize = 0,
            FileHash = "abc",
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = requestId,
            SourceDocumentId = doc.DocumentId,
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var processor = CreateProcessor(db, tempDir, slotService);

        try
        {
            // Unpack should fail safely due to missing file
            await processor.UnpackBatchAsync(batch.BatchId, CancellationToken.None);

            var updatedBatch = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch.BatchId);
            Assert.Equal(FilingBatchStatus.Failed, updatedBatch.Status);

            // Slot MUST be released in terminal failure path
            var leaseState = await db.OperationalSlotLeases.AsNoTracking()
                .FirstOrDefaultAsync(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot);
            Assert.True(leaseState == null || leaseState.ActiveHolderId == null);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.BatchId == batch.BatchId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.DocumentId == doc.DocumentId).ExecuteDeleteAsync();
            await db.OperationalSlotLeases.Where(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot).ExecuteDeleteAsync();
        }
    }
}
