using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        await global::MCAROC_Analysis.Tests.TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<long> EnsureTestRequestAsync(AppDbContext db)
    {
        var existing = await db.Requests.FirstOrDefaultAsync();
        if (existing != null) return existing.RequestId;

        var client = await db.Clients.FirstOrDefaultAsync();
        if (client == null)
        {
            client = new Client
            {
                ClientCode = "LEASE_" + Guid.NewGuid().ToString("N")[..6],
                ClientName = "Lease Test Client",
                CreatedDate = DateTime.UtcNow
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        var req = new McaRequest
        {
            ClientId = client.ClientId,
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

    private static FilingBatchProcessor CreateProcessor(AppDbContext db, string contentRootPath, IOperationalSlotLeaseService slotLeaseService, int? maxConcurrentUnpacks = null)
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
            slotLeaseService,
            maxConcurrentUnpacks is { } cap ? Options.Create(new LargeArchiveUploadOptions { MaxConcurrentUnpacks = cap }) : null);
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
            var ex = await Assert.ThrowsAsync<OperationalSlotBusyException>(() =>
                processor.UnpackBatchAsync(batch2.BatchId, CancellationToken.None));

            Assert.Contains("Cannot acquire 'LargeUnpack' slot lease", ex.Message);
            Assert.Equal("LargeUnpack", ex.SlotType);
            Assert.Equal(holder1, ex.ActiveHolderId);

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

    [Fact]
    public async Task Worker_BlockedBatch_ProceedsAfterHolderReleasesSlot_WithoutRestart()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "unpack-worker-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, "worker-batch.zip");
        var sha = CreateValidOuterZip(zipPath);

        var doc = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "worker-batch.zip",
            StoredFileName = "worker-batch.zip",
            StoragePath = zipPath,
            FileSize = new FileInfo(zipPath).Length,
            FileHash = sha,
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

        // 1. Initial holder acquires the LargeUnpackSlot, simulating another active unpack in progress
        const string initialHolder = "active-unpack-worker-1";
        await using (var setupDb = CreateContext())
        {
            var setupSlotService = new OperationalSlotLeaseService(setupDb, NullLogger<OperationalSlotLeaseService>.Instance);
            var initialLease = await setupSlotService.TryAcquireSlotAsync(
                OperationalSlotLeaseService.LargeUnpackSlot,
                initialHolder,
                TimeSpan.FromMinutes(5));
            Assert.True(initialLease.Success, initialLease.Error);
        }

        // 2. Set up worker with DI scope, shared queue, and bounded 150ms retry delay
        var queue = new FilingProcessingQueue();
        var chunkQueue = new DocumentChunkingQueue();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(ConnectionString));
        services.AddSingleton(queue);
        services.AddSingleton(chunkQueue);
        services.AddScoped<IOperationalSlotLeaseService, OperationalSlotLeaseService>();
        services.AddScoped(sp => new FilingBatchProcessor(
            sp.GetRequiredService<AppDbContext>(),
            tempDir,
            null,
            null,
            sp.GetRequiredService<FilingProcessingQueue>(),
            sp.GetRequiredService<DocumentChunkingQueue>(),
            NullLogger<FilingBatchProcessor>.Instance,
            sp.GetRequiredService<IOperationalSlotLeaseService>()));
        services.AddScoped<IStorageReservationManager, StorageReservationManager>();
        services.AddScoped<FinalizationRecoveryService>();
        services.AddSingleton(Options.Create(new LargeArchiveUploadOptions
        {
            SlotRetryDelay = TimeSpan.FromMilliseconds(150),
            MaxSlotRetryDelay = TimeSpan.FromSeconds(1)
        }));

        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var worker = new FilingProcessingWorker(
            scopeFactory,
            queue,
            NullLogger<FilingProcessingWorker>.Instance,
            serviceProvider.GetRequiredService<IOptions<LargeArchiveUploadOptions>>());

        using var workerCts = new CancellationTokenSource();
        await worker.StartAsync(workerCts.Token);

        try
        {
            // 3. Enqueue the batch while slot is held
            queue.Enqueue(new UnpackBatchWorkItem(batch.BatchId));

            // Wait for worker to dequeue, hit slot contention, and stand down
            await Task.Delay(500);

            // 4. Batch must retain Uploaded state in DB (not Failed, not Unpacking)
            var batchAfterContention = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch.BatchId);
            Assert.Equal(FilingBatchStatus.Uploaded, batchAfterContention.Status);

            // 5. Release the slot from the initial holder
            await using (var releaseDb = CreateContext())
            {
                var releaseSlotService = new OperationalSlotLeaseService(releaseDb, NullLogger<OperationalSlotLeaseService>.Instance);
                await releaseSlotService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUnpackSlot, initialHolder);
            }

            // 6. Prove the blocked batch automatically resumes and proceeds without restarting the application
            var deadline = DateTime.UtcNow.AddSeconds(20);
            FilingBatchStatus finalStatus = FilingBatchStatus.Uploaded;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
                var current = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch.BatchId);
                finalStatus = current.Status;
                if (finalStatus == FilingBatchStatus.Processing || finalStatus == FilingBatchStatus.Completed)
                    break;
            }

            Assert.True(
                finalStatus == FilingBatchStatus.Processing || finalStatus == FilingBatchStatus.Completed,
                $"Expected batch {batch.BatchId} to proceed to Processing or Completed after slot release, but status was {finalStatus}");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.BatchId == batch.BatchId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.DocumentId == doc.DocumentId).ExecuteDeleteAsync();
            await db.OperationalSlotLeases.Where(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot).ExecuteDeleteAsync();
        }
    }

    /// <summary>The throughput fix end to end: with MaxConcurrentUnpacks = 2, a second batch's
    /// UnpackBatchAsync no longer has to wait for the first to release the slot — both genuinely unpack at
    /// once, unlike UnpackBatchAsync_CannotBegin_WhenAnotherBatchHoldsLargeUnpackSlot above (the historical,
    /// still-default capacity-1 behavior).</summary>
    [Fact]
    public async Task UnpackBatchAsync_TwoBatchesProceedConcurrently_WhenCapacityIsTwo()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "unpack-capacity2-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var zip1Path = Path.Combine(tempDir, "batch1.zip");
        var zip2Path = Path.Combine(tempDir, "batch2.zip");
        var sha1 = CreateValidOuterZip(zip1Path);
        var sha2 = CreateValidOuterZip(zip2Path);

        var doc1 = new RequestDocument
        {
            RequestId = requestId, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "batch1.zip",
            StoredFileName = "batch1.zip", StoragePath = zip1Path, FileSize = new FileInfo(zip1Path).Length,
            FileHash = sha1, UploadStatus = DocumentUploadStatus.Uploaded, UploadedDate = DateTime.UtcNow, IsActiveSource = false
        };
        var doc2 = new RequestDocument
        {
            RequestId = requestId, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "batch2.zip",
            StoredFileName = "batch2.zip", StoragePath = zip2Path, FileSize = new FileInfo(zip2Path).Length,
            FileHash = sha2, UploadStatus = DocumentUploadStatus.Uploaded, UploadedDate = DateTime.UtcNow, IsActiveSource = false
        };
        db.RequestDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        var batch1 = new McaFilingBatch { RequestId = requestId, SourceDocumentId = doc1.DocumentId, Status = FilingBatchStatus.Uploaded, StartedDate = DateTime.UtcNow };
        var batch2 = new McaFilingBatch { RequestId = requestId, SourceDocumentId = doc2.DocumentId, Status = FilingBatchStatus.Uploaded, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.AddRange(batch1, batch2);
        await db.SaveChangesAsync();

        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var processor = CreateProcessor(db, tempDir, slotService, maxConcurrentUnpacks: 2);

        try
        {
            // Batch 1 acquires and holds LargeUnpackSlot directly (simulating it mid-unpack), exactly as
            // the capacity-1 test above does — the only difference is the capacity passed below.
            var holder1 = batch1.BatchId.ToString();
            var lease1 = await slotService.TryAcquireSlotAsync(OperationalSlotLeaseService.LargeUnpackSlot, holder1, TimeSpan.FromMinutes(5), capacity: 2);
            Assert.True(lease1.Success, lease1.Error);

            // Batch 2 must NOT be refused this time — capacity 2 admits both at once.
            await processor.UnpackBatchAsync(batch2.BatchId, CancellationToken.None);

            var updatedBatch2 = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.BatchId == batch2.BatchId);
            Assert.Equal(FilingBatchStatus.Processing, updatedBatch2.Status);

            // Batch 1's lease is still held (release the direct one taken above never ran) — proving both
            // genuinely coexisted rather than batch 2 having silently waited for batch 1 first.
            var stillHeld = await db.OperationalSlotLeases.AsNoTracking()
                .AnyAsync(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot && s.ActiveHolderId == holder1);
            Assert.True(stillHeld);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.BatchId == batch1.BatchId || b.BatchId == batch2.BatchId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.DocumentId == doc1.DocumentId || d.DocumentId == doc2.DocumentId).ExecuteDeleteAsync();
            await db.OperationalSlotLeases.Where(s => s.SlotType == OperationalSlotLeaseService.LargeUnpackSlot).ExecuteDeleteAsync();
        }
    }
}
