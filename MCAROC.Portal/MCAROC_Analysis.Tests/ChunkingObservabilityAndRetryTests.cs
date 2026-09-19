using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ChunkingObservabilityAndRetryTests : IAsyncLifetime
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

    private async Task<(McaRequest request, McaFilingBatch batch, McaFiling filing)> SeedFilingHierarchyAsync(AppDbContext db)
    {
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
            RequestStatus = RequestStatus.DocumentsUploaded,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);

        var doc = new RequestDocument
        {
            Request = request,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "filings.zip",
            StoredFileName = "filings.zip",
            StoragePath = @"C:\fake\filings.zip",
            FileHash = "hash1",
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            SourceDocumentId = doc.DocumentId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow
        };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();

        var filing = new McaFiling
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            Srn = $"SRN-{Guid.NewGuid():N}"[..10],
            NestedZipName = "1.zip",
            OuterCategoryFolder = "x"
        };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        return (request, batch, filing);
    }

    [Fact]
    public void ErrorClassification_MapsToBoundedCategories()
    {
        Assert.Equal("EmbeddingApi", DocumentChunkingOrchestrator.ClassifyChunkingError(new HttpRequestException("API failed")));
        Assert.Equal("TextFileMissing", DocumentChunkingOrchestrator.ClassifyChunkingError(new InvalidOperationException("Extracted text file not found for document 123.")));
        Assert.Equal("VectorMismatch", DocumentChunkingOrchestrator.ClassifyChunkingError(new InvalidOperationException("Embedding count 5 does not match chunk count 6 for document 123.")));
        Assert.Equal("DatabaseWrite", DocumentChunkingOrchestrator.ClassifyChunkingError(new DbUpdateException("DB error")));
        Assert.Equal("Unknown", DocumentChunkingOrchestrator.ClassifyChunkingError(new ArgumentException("Bad argument")));
    }

    [Fact]
    public async Task AuthoritativeBatchTotalChunks_UsesJoin_NotContains()
    {
        await using var db = CreateContext();
        var (_, batch, _) = await SeedFilingHierarchyAsync(db);

        var query = from chunk in db.DocumentChunks
                    join doc in db.McaFilingDocuments on chunk.FilingDocumentId equals doc.FilingDocumentId
                    where doc.BatchId == batch.BatchId
                    select chunk.ChunkId;

        var sql = query.ToQueryString();
        Assert.Contains("JOIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IN (", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuthoritativeBatchTotalChunks_ExcludesStaleBatches()
    {
        await using var db = CreateContext();
        var (request, staleBatch, filing1) = await SeedFilingHierarchyAsync(db);

        // Add 2nd authoritative batch for the same request
        var doc2 = new RequestDocument
        {
            Request = request,
            DocumentType = DocumentType.McaFilingsArchive,
            OriginalFileName = "filings2.zip",
            StoredFileName = "filings2.zip",
            StoragePath = @"C:\fake\filings2.zip",
            FileHash = "hash2",
            UploadedDate = DateTime.UtcNow
        };
        db.RequestDocuments.Add(doc2);
        await db.SaveChangesAsync();

        var authBatch = new McaFilingBatch
        {
            RequestId = request.RequestId,
            SourceDocumentId = doc2.DocumentId,
            Status = FilingBatchStatus.Completed,
            StartedDate = DateTime.UtcNow.AddMinutes(5)
        };
        db.McaFilingBatches.Add(authBatch);
        await db.SaveChangesAsync();

        var filing2 = new McaFiling
        {
            BatchId = authBatch.BatchId,
            RequestId = request.RequestId,
            Srn = "SRN2",
            NestedZipName = "2.zip",
            OuterCategoryFolder = "y"
        };
        db.McaFilings.Add(filing2);
        await db.SaveChangesAsync();

        // Stale batch doc + 10 chunks
        var staleDoc = new McaFilingDocument
        {
            BatchId = staleBatch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing1.FilingId,
            OriginalFileName = "stale.pdf",
            FileHash = "h_stale",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Chunked,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(staleDoc);
        await db.SaveChangesAsync();

        for (var i = 0; i < 10; i++)
        {
            db.DocumentChunks.Add(new DocumentChunk
            {
                RequestId = request.RequestId,
                BatchId = staleBatch.BatchId,
                FilingId = filing1.FilingId,
                FilingDocumentId = staleDoc.FilingDocumentId,
                Srn = filing1.Srn,
                Category = FilingCategory.Compliance,
                DocumentName = "stale.pdf",
                ChunkIndex = i,
                ChunkText = $"stale text {i}",
                Embedding = new SqlVector<float>(new float[768]),
                EmbeddingModel = "test",
                EmbeddingDimensions = 768,
                ChunkingVersion = "1.0",
                CreatedDate = DateTime.UtcNow
            });
        }

        // Authoritative batch doc + 5 chunks
        var authDoc = new McaFilingDocument
        {
            BatchId = authBatch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing2.FilingId,
            OriginalFileName = "auth.pdf",
            FileHash = "h_auth",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Chunked,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(authDoc);
        await db.SaveChangesAsync();

        for (var i = 0; i < 5; i++)
        {
            db.DocumentChunks.Add(new DocumentChunk
            {
                RequestId = request.RequestId,
                BatchId = authBatch.BatchId,
                FilingId = filing2.FilingId,
                FilingDocumentId = authDoc.FilingDocumentId,
                Srn = filing2.Srn,
                Category = FilingCategory.Compliance,
                DocumentName = "auth.pdf",
                ChunkIndex = i,
                ChunkText = $"auth text {i}",
                Embedding = new SqlVector<float>(new float[768]),
                EmbeddingModel = "test",
                EmbeddingDimensions = 768,
                ChunkingVersion = "1.0",
                CreatedDate = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();

        var authCount = await (
            from chunk in db.DocumentChunks
            join d in db.McaFilingDocuments on chunk.FilingDocumentId equals d.FilingDocumentId
            where d.BatchId == authBatch.BatchId
            select chunk.ChunkId
        ).CountAsync();

        Assert.Equal(5, authCount);
    }

    [Fact]
    public async Task RetryFailedDocumentAsync_ResetsDocumentAndLeavesLastAttemptIntact()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingHierarchyAsync(db);

        var priorAttempt = DateTime.UtcNow.AddMinutes(-10);
        var doc = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "failed.pdf",
            FileHash = "h_fail",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Failed,
            ChunkRetryCount = 3,
            ChunkingLastError = "API 503 Service Unavailable",
            ChunkingErrorCategory = "EmbeddingApi",
            ChunkingFailedUtc = DateTime.UtcNow.AddMinutes(-5),
            ChunkingLastAttemptUtc = priorAttempt,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var queue = new DocumentChunkingQueue();
        var orchestrator = new DocumentChunkingOrchestrator(db, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance);

        var reset = await orchestrator.RetryFailedDocumentAsync(doc.FilingDocumentId, batch.BatchId, CancellationToken.None);

        Assert.True(reset);
        await db.Entry(doc).ReloadAsync();

        Assert.Equal(ChunkingStatus.Pending, doc.ChunkingStatus);
        Assert.Equal(0, doc.ChunkRetryCount);
        Assert.Null(doc.ChunkingLastError);
        Assert.Null(doc.ChunkingErrorCategory);
        Assert.Null(doc.ChunkingFailedUtc);
        // Last attempt must NOT be overwritten by retry reset:
        Assert.Equal(priorAttempt.ToString("s"), doc.ChunkingLastAttemptUtc?.ToString("s"));
    }

    [Fact]
    public async Task RetryFailedDocumentAsync_Guards_RejectNonTerminal_Duplicate_OrIncomplete()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingHierarchyAsync(db);

        // Canonical completed document
        var canonical = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "canonical.pdf",
            FileHash = "h_c",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Pending, // not Failed!
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(canonical);
        await db.SaveChangesAsync();

        // Duplicate document
        var duplicate = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "dupe.pdf",
            FileHash = "h_c",
            DuplicateOfDocumentId = canonical.FilingDocumentId,
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Failed,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(duplicate);

        // Incomplete processing document
        var incomplete = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "incomp.pdf",
            FileHash = "h_inc",
            ProcessingStatus = FilingDocumentProcessingStatus.TextExtracting, // Not Completed
            ChunkingStatus = ChunkingStatus.Failed,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(incomplete);
        await db.SaveChangesAsync();

        var queue = new DocumentChunkingQueue();
        var orchestrator = new DocumentChunkingOrchestrator(db, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance);

        // Guard 1: Not Failed
        Assert.False(await orchestrator.RetryFailedDocumentAsync(canonical.FilingDocumentId, batch.BatchId, CancellationToken.None));
        // Guard 2: Duplicate
        Assert.False(await orchestrator.RetryFailedDocumentAsync(duplicate.FilingDocumentId, batch.BatchId, CancellationToken.None));
        // Guard 3: ProcessingStatus != Completed
        Assert.False(await orchestrator.RetryFailedDocumentAsync(incomplete.FilingDocumentId, batch.BatchId, CancellationToken.None));
    }

    [Fact]
    public async Task RetryFailedDocumentAsync_ConcurrentCalls_AreIdempotent()
    {
        await using var db = CreateContext();
        var (request, batch, filing) = await SeedFilingHierarchyAsync(db);

        var doc = new McaFilingDocument
        {
            BatchId = batch.BatchId,
            RequestId = request.RequestId,
            FilingId = filing.FilingId,
            OriginalFileName = "concurrent.pdf",
            FileHash = "h_conc",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Failed,
            ChunkRetryCount = 3,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(doc);
        await db.SaveChangesAsync();

        var queue = new DocumentChunkingQueue();

        // Run 5 concurrent retries, each on its own DbContext (simulating concurrent web requests)
        var tasks = Enumerable.Range(0, 5)
            .Select(async _ =>
            {
                await using var taskDb = CreateContext();
                var orchestrator = new DocumentChunkingOrchestrator(taskDb, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance);
                return await orchestrator.RetryFailedDocumentAsync(doc.FilingDocumentId, batch.BatchId, CancellationToken.None);
            })
            .ToList();

        var results = await Task.WhenAll(tasks);
        Assert.Single(results, true);
        Assert.Equal(4, results.Count(r => !r));
    }

    [Fact]
    public async Task RetryDocumentChunkingEndpoint_OwnershipGuard_ReturnsNotFound()
    {
        await using var db = CreateContext();
        var (request1, batch1, _) = await SeedFilingHierarchyAsync(db);
        var (request2, batch2, filing2) = await SeedFilingHierarchyAsync(db);

        // Document in request 2
        var docInReq2 = new McaFilingDocument
        {
            BatchId = batch2.BatchId,
            RequestId = request2.RequestId,
            FilingId = filing2.FilingId,
            OriginalFileName = "other.pdf",
            FileHash = "h_other",
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Failed,
            UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(docInReq2);
        await db.SaveChangesAsync();

        var queue = new DocumentChunkingQueue();
        var orchestrator = new DocumentChunkingOrchestrator(db, null!, queue, NullLogger<DocumentChunkingOrchestrator>.Instance);
        var controller = new RequestsController(db, null!, null!, null!, null!, null!, Dossier.DossierGoldenMasterTests.CreateCache(), null!, null!);

        // Call endpoint for request 1 with document belonging to request 2 -> 404
        var result = await controller.RetryDocumentChunking(request1.RequestId, docInReq2.FilingDocumentId, orchestrator, CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }
}
