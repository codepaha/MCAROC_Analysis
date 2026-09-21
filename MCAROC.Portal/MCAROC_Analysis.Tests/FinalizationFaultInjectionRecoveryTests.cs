using System.IO.Compression;
using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FinalizationFaultInjectionRecoveryTests : IAsyncLifetime
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
                ClientCode = "FAULT_" + Guid.NewGuid().ToString("N")[..6],
                ClientName = "Fault Recovery Client",
                CreatedDate = DateTime.UtcNow
            };
            db.Clients.Add(client);
            await db.SaveChangesAsync();
        }

        var req = new McaRequest
        {
            ClientId = client.ClientId,
            RequestNumber = "REQ-" + Guid.NewGuid().ToString("N")[..8],
            CompanyName = "Test Fault Recovery Co",
            Cin = "U12345MH2026PTC123456",
            RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        return req.RequestId;
    }

    private static string CreateValidZip(string path, string entryName = "doc.txt", string content = "hello fault injection")
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
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

    private static IOptions<LargeArchiveUploadOptions> CreateTestOptions() =>
        Options.Create(new LargeArchiveUploadOptions
        {
            MaxUncompressedSizeBytes = 10 * 1024 * 1024,
            MaxNestedTempBytes = 1 * 1024 * 1024,
            MinFreeDiskHeadroomBytes = 1 * 1024 * 1024,
            ChunkSizeBytes = 64 * 1024
        });

    [Fact]
    public async Task Recovery_CrashAfterMove_CreatesDocumentAndBatchAndTransitionsReservation()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "rec-crash-move-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(tempDir, "staging");
        var destDir = Path.Combine(tempDir, "dest");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(destDir);

        var destZip = Path.Combine(destDir, "archive.zip");
        var stagingPart = Path.Combine(stagingDir, "archive.part");
        var sha = CreateValidZip(destZip);
        var fileSize = new FileInfo(destZip).Length;

        var sessionId = Guid.NewGuid();
        var clientToken = "token_" + Guid.NewGuid().ToString("N");

        var opts = CreateTestOptions();
        var resManager = new StorageReservationManager(db, opts, NullLogger<StorageReservationManager>.Instance);
        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var queue = new FilingProcessingQueue();

        // 1. Storage reservation acquired for UploadSession
        var reserveResult = await resManager.TryReserveUploadCapacityAsync(sessionId, fileSize, stagingDir, destDir);
        Assert.True(reserveResult.Success, reserveResult.Error);

        // 2. Session state: crashed after move (Status = ArchiveMoved, lease expired)
        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = requestId,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash(clientToken),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fileSize,
            NextExpectedOffset = fileSize,
            ExpectedFullSha256 = sha,
            Status = LargeArchiveUploadSessionStatus.ArchiveMoved,
            StagingFilePath = stagingPart,
            DestinationStoragePath = destZip,
            FinalizationAttemptId = Guid.NewGuid(),
            ActiveFinalizationExpiresUtc = DateTime.UtcNow.AddSeconds(-10),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            var service = new FinalizationRecoveryService(db, opts, resManager, slotService, queue, NullLogger<FinalizationRecoveryService>.Instance);

            // 3. Run recovery reconciliation
            var recovered = await service.ReconcileIncompleteFinalizationsAsync();
            Assert.Equal(1, recovered);

            // 4. Verify exactly 1 document and 1 batch created
            var docs = await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(docs);
            Assert.Equal(DocumentType.McaFilingsArchive, docs[0].DocumentType);
            Assert.False(docs[0].IsActiveSource);

            var batches = await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(batches);
            Assert.Equal(docs[0].DocumentId, batches[0].SourceDocumentId);

            // 5. Verify session completed and references document & batch
            var updatedSession = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(LargeArchiveUploadSessionStatus.Completed, updatedSession.Status);
            Assert.Equal(docs[0].DocumentId, updatedSession.CreatedDocumentId);
            Assert.Equal(batches[0].BatchId, updatedSession.CreatedBatchId);

            // 6. Verify storage reservation was transitioned to batch, NOT released
            var reservations = await db.StorageCapacityReservations
                .Where(r => r.OwnerId == batches[0].BatchId.ToString() && r.OwnerType == "FilingBatch")
                .ToListAsync();
            Assert.NotEmpty(reservations);
            Assert.All(reservations, r => Assert.Equal(StorageCapacityReservationState.Active, r.State));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.StorageCapacityReservations.Where(r => r.OwnerId == sessionId.ToString()).ExecuteDeleteAsync();
            await db.LargeArchiveUploadSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Recovery_CrashAfterDocumentCreation_ReusesExistingDocumentAndCompletes()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "rec-crash-doc-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(tempDir, "staging");
        var destDir = Path.Combine(tempDir, "dest");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(destDir);

        var destZip = Path.Combine(destDir, "archive.zip");
        var stagingPart = Path.Combine(stagingDir, "archive.part");
        var sha = CreateValidZip(destZip);
        var fileSize = new FileInfo(destZip).Length;

        var sessionId = Guid.NewGuid();
        var clientToken = "token_" + Guid.NewGuid().ToString("N");

        var opts = CreateTestOptions();
        var resManager = new StorageReservationManager(db, opts, NullLogger<StorageReservationManager>.Instance);
        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var queue = new FilingProcessingQueue();
        var res2 = await resManager.TryReserveUploadCapacityAsync(sessionId, fileSize, stagingDir, destDir);
        Assert.True(res2.Success, res2.Error);

        // Pre-create RequestDocument (simulating crash immediately after Document insert)
        var preDoc = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "archive.zip",
            StoredFileName = Path.GetFileName(destZip),
            StoragePath = destZip,
            FileSize = fileSize,
            FileHash = sha,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false,
            UploadSessionId = sessionId
        };
        db.RequestDocuments.Add(preDoc);
        await db.SaveChangesAsync();

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = requestId,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash(clientToken),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fileSize,
            NextExpectedOffset = fileSize,
            ExpectedFullSha256 = sha,
            Status = LargeArchiveUploadSessionStatus.DocumentCommitted,
            CreatedDocumentId = preDoc.DocumentId,
            StagingFilePath = stagingPart,
            DestinationStoragePath = destZip,
            FinalizationAttemptId = Guid.NewGuid(),
            ActiveFinalizationExpiresUtc = DateTime.UtcNow.AddSeconds(-10),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            var service = new FinalizationRecoveryService(db, opts, resManager, slotService, queue, NullLogger<FinalizationRecoveryService>.Instance);

            var recovered = await service.ReconcileIncompleteFinalizationsAsync();
            Assert.Equal(1, recovered);

            // Document must be reused, no duplicates created
            var docs = await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(docs);
            Assert.Equal(preDoc.DocumentId, docs[0].DocumentId);

            // Batch created
            var batches = await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(batches);
            Assert.Equal(preDoc.DocumentId, batches[0].SourceDocumentId);

            var updatedSession = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(LargeArchiveUploadSessionStatus.Completed, updatedSession.Status);
            Assert.Equal(preDoc.DocumentId, updatedSession.CreatedDocumentId);
            Assert.Equal(batches[0].BatchId, updatedSession.CreatedBatchId);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.StorageCapacityReservations.Where(r => r.OwnerId == sessionId.ToString()).ExecuteDeleteAsync();
            await db.LargeArchiveUploadSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Recovery_CrashAfterBatchCreation_ReusesExistingBatchAndCompletes()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "rec-crash-batch-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(tempDir, "staging");
        var destDir = Path.Combine(tempDir, "dest");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(destDir);

        var destZip = Path.Combine(destDir, "archive.zip");
        var stagingPart = Path.Combine(stagingDir, "archive.part");
        var sha = CreateValidZip(destZip);
        var fileSize = new FileInfo(destZip).Length;

        var sessionId = Guid.NewGuid();
        var clientToken = "token_" + Guid.NewGuid().ToString("N");

        var opts = CreateTestOptions();
        var resManager = new StorageReservationManager(db, opts, NullLogger<StorageReservationManager>.Instance);
        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var queue = new FilingProcessingQueue();

        var res3 = await resManager.TryReserveUploadCapacityAsync(sessionId, fileSize, stagingDir, destDir);
        Assert.True(res3.Success, res3.Error);

        var preDoc = new RequestDocument
        {
            RequestId = requestId,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "archive.zip",
            StoredFileName = Path.GetFileName(destZip),
            StoragePath = destZip,
            FileSize = fileSize,
            FileHash = sha,
            UploadStatus = DocumentUploadStatus.Uploaded,
            UploadedDate = DateTime.UtcNow,
            IsActiveSource = false,
            UploadSessionId = sessionId
        };
        db.RequestDocuments.Add(preDoc);
        await db.SaveChangesAsync();

        var preBatch = new McaFilingBatch
        {
            RequestId = requestId,
            SourceDocumentId = preDoc.DocumentId,
            Status = FilingBatchStatus.Uploaded,
            StartedDate = DateTime.UtcNow,
            UploadSessionId = sessionId
        };
        db.McaFilingBatches.Add(preBatch);
        await db.SaveChangesAsync();

        // Transition reservation to batch
        await resManager.TransitionReservationToBatchAsync(sessionId, preBatch.BatchId);

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = requestId,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash(clientToken),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fileSize,
            NextExpectedOffset = fileSize,
            ExpectedFullSha256 = sha,
            Status = LargeArchiveUploadSessionStatus.BatchCreated,
            CreatedDocumentId = preDoc.DocumentId,
            CreatedBatchId = preBatch.BatchId,
            StagingFilePath = stagingPart,
            DestinationStoragePath = destZip,
            FinalizationAttemptId = Guid.NewGuid(),
            ActiveFinalizationExpiresUtc = DateTime.UtcNow.AddSeconds(-10),
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            var service = new FinalizationRecoveryService(db, opts, resManager, slotService, queue, NullLogger<FinalizationRecoveryService>.Instance);

            var recovered = await service.ReconcileIncompleteFinalizationsAsync();
            Assert.Equal(1, recovered);

            var docs = await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(docs);
            Assert.Equal(preDoc.DocumentId, docs[0].DocumentId);

            var batches = await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ToListAsync();
            Assert.Single(batches);
            Assert.Equal(preBatch.BatchId, batches[0].BatchId);

            var updatedSession = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(LargeArchiveUploadSessionStatus.Completed, updatedSession.Status);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.McaFilingBatches.Where(b => b.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.RequestDocuments.Where(d => d.UploadSessionId == sessionId).ExecuteDeleteAsync();
            await db.StorageCapacityReservations.Where(r => r.OwnerId == preBatch.BatchId.ToString()).ExecuteDeleteAsync();
            await db.LargeArchiveUploadSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task Finalize_PreMoveHashMismatch_FailsSessionAndReleasesReservation()
    {
        await using var db = CreateContext();
        var requestId = await EnsureTestRequestAsync(db);

        var tempDir = Path.Combine(Path.GetTempPath(), "rec-hash-fail-" + Guid.NewGuid().ToString("N"));
        var stagingDir = Path.Combine(tempDir, "staging");
        var destDir = Path.Combine(tempDir, "dest");
        Directory.CreateDirectory(stagingDir);
        Directory.CreateDirectory(destDir);

        var stagingPart = Path.Combine(stagingDir, "archive.part");
        var destZip = Path.Combine(destDir, "archive.zip");
        CreateValidZip(stagingPart, content: "actual content");
        var fileSize = new FileInfo(stagingPart).Length;

        var sessionId = Guid.NewGuid();
        var clientToken = "token_" + Guid.NewGuid().ToString("N");

        var opts = CreateTestOptions();
        var resManager = new StorageReservationManager(db, opts, NullLogger<StorageReservationManager>.Instance);
        var slotService = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var queue = new FilingProcessingQueue();

        // Storage reservation acquired
        var res = await resManager.TryReserveUploadCapacityAsync(sessionId, fileSize, stagingDir, destDir);
        Assert.True(res.Success, res.Error);

        // Upload session has wrong expected hash
        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = requestId,
            HashedCapabilityToken = ChunkStreamingService.ComputeTokenHash(clientToken),
            OriginalFileName = "archive.zip",
            TotalExpectedSizeBytes = fileSize,
            NextExpectedOffset = fileSize,
            ExpectedFullSha256 = "0000000000000000000000000000000000000000000000000000000000000000",
            Status = LargeArchiveUploadSessionStatus.Uploading,
            StagingFilePath = stagingPart,
            DestinationStoragePath = destZip,
            CreatedUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.AddHours(1)
        };
        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync();

        try
        {
            var service = new FinalizationRecoveryService(db, opts, resManager, slotService, queue, NullLogger<FinalizationRecoveryService>.Instance);

            var result = await service.CompleteSessionAsync(sessionId, clientToken);
            Assert.False(result.Success);

            // Session must be marked Failed
            var updatedSession = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId);
            Assert.Equal(LargeArchiveUploadSessionStatus.Failed, updatedSession.Status);
            Assert.Contains("SHA-256 mismatch", updatedSession.FailureReason);

            // Storage reservation must be Released on pre-move abort
            var reservations = await db.StorageCapacityReservations
                .AsNoTracking()
                .Where(r => r.OwnerId == sessionId.ToString() && r.OwnerType == "UploadSession")
                .ToListAsync();
            Assert.NotEmpty(reservations);
            Assert.All(reservations, r => Assert.Equal(StorageCapacityReservationState.Released, r.State));

            // Archive was NOT moved to destination
            Assert.True(File.Exists(stagingPart));
            Assert.False(File.Exists(destZip));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            await db.StorageCapacityReservations.Where(r => r.OwnerId == sessionId.ToString()).ExecuteDeleteAsync();
            await db.LargeArchiveUploadSessions.Where(s => s.SessionId == sessionId).ExecuteDeleteAsync();
        }
    }
}
