using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Regression coverage for the review finding that DocumentChunkingOrchestrator.RecoverStaleWorkAsync
/// only re-enqueued batches recovered from a crashed InProgress state, never batches whose documents sat at
/// ChunkingStatus.Pending with nothing ever having enqueued their batch (e.g. every pre-existing Completed
/// document the AddDocumentChatEngine migration backfills to Pending) — those would never get chunked.
/// DocumentChunkingOrchestrator itself isn't constructed here since its EmbeddingService dependency eagerly
/// loads Google Cloud credentials from disk (the same accepted testing boundary as Phase 2/3's AI services);
/// these tests mirror RecoverStaleWorkAsync's exact predicates directly against a real SQL Server test
/// database, the same style FilingClaimSemanticsTests uses for Phase 2's claim predicates.</summary>
public class DocumentChunkingRecoveryTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<(McaRequest request, McaFilingBatch batch, McaFiling filing)> SeedFilingAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"TEST-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        var doc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip",
            StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = doc.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "1", NestedZipName = "1.zip", OuterCategoryFolder = "x" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        return (request, batch, filing);
    }

    // Mirrors RecoverStaleWorkAsync's exact query for batches with an eligible Pending backlog.
    private static Task<List<long>> PendingBackfillBatchIdsAsync(AppDbContext db) =>
        db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.BatchId)
            .Distinct()
            .ToListAsync();

    [Fact]
    public async Task PreExistingCompletedDocumentsLeftPending_HaveTheirBatchIdentifiedForRequeue()
    {
        // The exact scenario the migration backfill creates: a document that finished Phase 2's pipeline
        // long before Phase 4 existed sits at ChunkingStatus.Pending, but MaybeCompleteBatchAsync's
        // enqueue only fires when a batch *newly* reaches Completed — this batch never will again.
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);
        db.McaFilingDocuments.Add(new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Pending, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var batchIds = await PendingBackfillBatchIdsAsync(db);

        Assert.Contains(batch.BatchId, batchIds);
    }

    [Fact]
    public async Task DuplicateDocumentLeftPending_IsNeverEnqueuedOnItsOwn()
    {
        // A duplicate document is never independently chunked (it reuses the canonical document's
        // embeddings) — it should never surface in the pending-backfill query on its own.
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);
        var canonical = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Chunked, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(canonical);
        await db.SaveChangesAsync();
        db.McaFilingDocuments.Add(new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a-dup.pdf", StoragePath = @"C:\fake\a-dup.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Pending, DuplicateOfDocumentId = canonical.FilingDocumentId, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var batchIds = await PendingBackfillBatchIdsAsync(db);

        Assert.DoesNotContain(batch.BatchId, batchIds);
    }

    [Fact]
    public async Task AlreadyChunkedDocument_DoesNotTriggerRequeue()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingAsync(db);
        db.McaFilingDocuments.Add(new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Chunked, UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var batchIds = await PendingBackfillBatchIdsAsync(db);

        Assert.DoesNotContain(batch.BatchId, batchIds);
    }
}
