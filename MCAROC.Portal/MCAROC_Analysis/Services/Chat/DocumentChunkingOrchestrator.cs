using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Chunks and embeds one McaFilingDocument at a time. Mirrors Phase 2/3's atomic-claim pattern
/// exactly. Rollback-safe re-chunk: chunking and embedding (the parts that can fail) happen entirely before
/// touching the database — only once the full new chunk set is computed does one transaction delete the
/// document's existing chunks and insert the new set together, so a failure never destroys a previously
/// working index.</summary>
public class DocumentChunkingOrchestrator(AppDbContext db, EmbeddingService embeddingService, DocumentChunkingQueue queue, ILogger<DocumentChunkingOrchestrator> logger)
{
    public const string ChunkingVersion = "1.0";
    private const int MaxChunkRetryCount = 3;

    public async Task<List<long>> GetPendingDocumentIdsAsync(long batchId, CancellationToken ct) =>
        await db.McaFilingDocuments
            .Where(d => d.BatchId == batchId && d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.FilingDocumentId)
            .ToListAsync(ct);

    public async Task ChunkDocumentAsync(long filingDocumentId, CancellationToken ct)
    {
        var claimed = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == filingDocumentId && d.ChunkingStatus == ChunkingStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress), ct);
        if (claimed == 0)
            return; // already claimed/chunked by another worker or a previous run

        var document = await db.McaFilingDocuments.Include(d => d.Filing).FirstAsync(d => d.FilingDocumentId == filingDocumentId, ct);

        try
        {
            if (document.ExtractedTextPath is null || !File.Exists(document.ExtractedTextPath))
                throw new InvalidOperationException($"Extracted text file not found for document {filingDocumentId}.");

            var fullText = await File.ReadAllTextAsync(document.ExtractedTextPath, ct);
            var textChunks = TextChunker.Chunk(fullText, ChatIndexingOptions.Default);
            var embeddings = textChunks.Count > 0
                ? await embeddingService.EmbedDocumentsAsync(textChunks.Select(c => c.Text).ToList(), ct)
                : [];
            // Chunks are paired with embeddings positionally below; a mismatch means the embedding call
            // dropped/duplicated a vector — fail this document (retry, then Failed) rather than persist a
            // misaligned or truncated index.
            if (embeddings.Count != textChunks.Count)
                throw new InvalidOperationException(
                    $"Embedding count {embeddings.Count} does not match chunk count {textChunks.Count} for document {filingDocumentId}.");

            // Duplicates (same file hash) never went through their own text extraction — Phase 2 marks
            // them Skipped at discovery and reuses the canonical copy's results — so they get their own
            // DocumentChunk rows here too, reusing these already-computed embeddings but stamped with
            // their own FilingId/Srn/DocumentName. The embedding API call above happens exactly once
            // regardless of how many duplicates share this text.
            var duplicates = await db.McaFilingDocuments.Include(d => d.Filing)
                .Where(d => d.DuplicateOfDocumentId == document.FilingDocumentId)
                .ToListAsync(ct);
            var targets = new List<McaFilingDocument> { document };
            targets.AddRange(duplicates);

            var newChunks = new List<DocumentChunk>();
            foreach (var target in targets)
            {
                for (var i = 0; i < textChunks.Count; i++)
                {
                    newChunks.Add(new DocumentChunk
                    {
                        RequestId = target.RequestId,
                        FilingDocumentId = target.FilingDocumentId,
                        FilingId = target.FilingId,
                        BatchId = target.BatchId,
                        Srn = target.Filing?.Srn ?? "",
                        Category = target.Category,
                        FormType = target.FormType,
                        DocumentName = target.OriginalFileName,
                        ChunkIndex = i,
                        PageNumber = textChunks[i].PageNumber,
                        ChunkText = textChunks[i].Text,
                        Embedding = new SqlVector<float>(embeddings[i]),
                        EmbeddingModel = EmbeddingService.ModelId,
                        EmbeddingDimensions = EmbeddingService.Dimensions,
                        ChunkingVersion = ChunkingVersion,
                        CreatedDate = DateTime.UtcNow
                    });
                }
            }

            var targetIds = targets.Select(t => t.FilingDocumentId).ToList();

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.DocumentChunks.Where(c => targetIds.Contains(c.FilingDocumentId)).ExecuteDeleteAsync(ct);
            db.DocumentChunks.AddRange(newChunks);
            await db.SaveChangesAsync(ct);
            await db.McaFilingDocuments.Where(d => targetIds.Contains(d.FilingDocumentId))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.Chunked), ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunking failed for document {DocumentId}", filingDocumentId);
            var retryCount = document.ChunkRetryCount + 1;
            var nextStatus = retryCount < MaxChunkRetryCount ? ChunkingStatus.Pending : ChunkingStatus.Failed;
            await db.McaFilingDocuments.Where(d => d.FilingDocumentId == filingDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, nextStatus)
                    .SetProperty(d => d.ChunkRetryCount, retryCount), ct);
            if (nextStatus == ChunkingStatus.Pending)
                queue.Enqueue(document.BatchId);
        }
    }

    /// <summary>Startup recovery: any document left InProgress by a crash is, by the same "fresh process =
    /// orphaned" logic already proven twice in this codebase (Phase 2/3), reset to Pending and its batch
    /// re-enqueued unconditionally — no staleness timeout at startup. Separately, every batch with at least
    /// one eligible Pending document is also re-enqueued, not just batches recovered from InProgress here:
    /// a document can reach ChunkingStatus.Pending without its batch ever having been enqueued — the
    /// migration backfills every pre-existing Completed document to Pending, but
    /// FilingBatchProcessor.MaybeCompleteBatchAsync only enqueues a batch at the moment it *newly* reaches
    /// Completed/CompletedWithErrors, which a batch that completed before Phase 4 existed never does again.
    /// Sweeping for eligible Pending batches unconditionally on every startup closes that gap regardless of
    /// how a document ended up Pending with nothing watching its batch.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var staleDocuments = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null && d.ChunkingStatus == ChunkingStatus.InProgress)
            .Select(d => new { d.FilingDocumentId, d.BatchId })
            .ToListAsync(ct);

        if (staleDocuments.Count > 0)
        {
            await db.McaFilingDocuments
                .Where(d => d.DuplicateOfDocumentId == null && d.ChunkingStatus == ChunkingStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.Pending), ct);
        }

        var pendingBatchIds = await db.McaFilingDocuments
            .Where(d => d.DuplicateOfDocumentId == null
                && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.BatchId)
            .Distinct()
            .ToListAsync(ct);

        var batchIdsToEnqueue = staleDocuments.Select(d => d.BatchId).Concat(pendingBatchIds).Distinct().ToList();
        foreach (var batchId in batchIdsToEnqueue)
            queue.Enqueue(batchId);

        return batchIdsToEnqueue.Count;
    }
}
