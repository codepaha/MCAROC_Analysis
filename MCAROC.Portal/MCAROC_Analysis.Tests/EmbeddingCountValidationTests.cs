using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

public class EmbeddingBatchCompleteGuardTests
{
    [Theory]
    [InlineData(2, 3)] // short (dropped a vector)
    [InlineData(4, 3)] // surplus (extra vector)
    [InlineData(0, 3)] // null / empty response
    public void EnsureBatchComplete_throws_on_count_mismatch(int returned, int input) =>
        Assert.Throws<InvalidOperationException>(() => EmbeddingService.EnsureBatchComplete(returned, input));

    [Fact]
    public void EnsureBatchComplete_is_a_no_op_when_counts_match() =>
        EmbeddingService.EnsureBatchComplete(3, 3);
}

/// <summary>The orchestrator must not persist any chunks (or mark the document Chunked) when the embedding
/// service returns a vector count that doesn't match the chunk count — otherwise chunk text and vectors
/// would be misaligned. Real .\SQLEXPRESS test DB; a stub EmbeddingService supplies the bad count.</summary>
public class DocumentChunkingEmbeddingMismatchTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly List<string> _tempFiles = [];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
        return Task.CompletedTask;
    }

    private sealed class StubEmbeddingService(Func<int, List<float[]>> map) : EmbeddingService
    {
        public override Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult(map(texts.Count));
    }

    private string WriteTempText(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chunk-mismatch-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public async Task Fewer_embeddings_than_chunks_persists_no_chunks_and_does_not_mark_chunked()
    {
        await using var db = CreateContext();

        var client = new Client { ClientCode = $"EMM{Guid.NewGuid():N}"[..10], ClientName = "Test Client", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"EMM-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DocumentsUploaded, CreatedDate = DateTime.UtcNow
        };
        var doc = new RequestDocument
        {
            Request = request, DocumentType = DocumentType.McaFilingsArchive, OriginalFileName = "f.zip",
            StoredFileName = "f.zip", StoragePath = @"C:\fake\f.zip", FileHash = "h", UploadedDate = DateTime.UtcNow
        };
        db.AddRange(client, request, doc);
        await db.SaveChangesAsync();

        var batch = new McaFilingBatch { RequestId = request.RequestId, SourceDocumentId = doc.DocumentId, Status = FilingBatchStatus.Completed, StartedDate = DateTime.UtcNow };
        db.McaFilingBatches.Add(batch);
        await db.SaveChangesAsync();
        var filing = new McaFiling { BatchId = batch.BatchId, RequestId = request.RequestId, Srn = "1", NestedZipName = "1.zip", OuterCategoryFolder = "x" };
        db.McaFilings.Add(filing);
        await db.SaveChangesAsync();

        // ~3000 chars over two paragraphs -> at least two chunks at the default 2000-char cap.
        var textPath = WriteTempText($"--- Page 1 (native) ---\n{new string('a', 1500)}\n\n{new string('b', 1500)}");
        var filingDoc = new McaFilingDocument
        {
            FilingId = filing.FilingId, BatchId = batch.BatchId, RequestId = request.RequestId,
            OriginalFileName = "a.pdf", StoragePath = @"C:\fake\a.pdf", FileHash = "h1",
            ExtractedTextPath = textPath,
            ProcessingStatus = FilingDocumentProcessingStatus.Completed,
            ChunkingStatus = ChunkingStatus.Pending, UpdatedAt = DateTime.UtcNow
        };
        db.McaFilingDocuments.Add(filingDoc);
        await db.SaveChangesAsync();

        // Stub returns exactly one vector no matter how many chunks were requested.
        var stub = new StubEmbeddingService(_ => [new float[EmbeddingService.Dimensions]]);
        var orchestrator = new DocumentChunkingOrchestrator(db, stub, new DocumentChunkingQueue(), NullLogger<DocumentChunkingOrchestrator>.Instance);

        await orchestrator.ChunkDocumentAsync(filingDoc.FilingDocumentId, CancellationToken.None);

        await using var verify = CreateContext();
        var reloaded = await verify.McaFilingDocuments.AsNoTracking().FirstAsync(d => d.FilingDocumentId == filingDoc.FilingDocumentId);
        Assert.NotEqual(ChunkingStatus.Chunked, reloaded.ChunkingStatus);
        Assert.Equal(1, reloaded.ChunkRetryCount);
        Assert.False(await verify.DocumentChunks.AnyAsync(c => c.FilingDocumentId == filingDoc.FilingDocumentId));
    }
}
