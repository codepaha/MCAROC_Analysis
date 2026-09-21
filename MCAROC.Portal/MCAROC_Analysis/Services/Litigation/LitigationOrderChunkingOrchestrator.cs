using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
// DocumentChunkingOrchestrator (Services.Chat) supplies ClassifyChunkingError/SanitizeAndCap — see the
// class doc comment below for why these are reused rather than duplicated.

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Chunks and embeds one <see cref="LitigationOrderDocument"/>'s extracted text at a time
/// (#244/LIT-04) — the litigation-specific counterpart to <c>Services.Chat.DocumentChunkingOrchestrator</c>,
/// mirroring its atomic-claim and rollback-safe re-chunk shape exactly (chunking/embedding — the parts that
/// can fail — happen entirely before touching the database; only once the full new chunk set is computed
/// does one transaction delete the document's existing chunks and insert the new set together, so a failure
/// never destroys a previously working index). Reuses <see cref="TextChunker"/> and <see cref="EmbeddingService"/>
/// unmodified — both are already fully generic — and <c>DocumentChunkingOrchestrator</c>'s own
/// <c>ClassifyChunkingError</c>/<c>SanitizeAndCap</c> static helpers rather than duplicating that regex-heavy
/// PII/secret-redaction logic, which has nothing McaFiling-specific about it.
///
/// Deliberately per-document, not per-batch like <c>DocumentChunkingOrchestrator</c>: litigation orders have
/// no batch concept (see <see cref="LitigationOrderChunkingQueue"/>'s own remarks), so there is no analogous
/// "list pending documents for this batch" fan-out step — the queue already carries exactly one order
/// document id per unit of work.
///
/// The claim query below checks only <c>ChunkingStatus == Pending</c>, not download/extraction state —
/// mirrors <c>DocumentChunkingOrchestrator.ChunkDocumentAsync</c>'s own claim exactly, relying entirely on
/// the trigger (<c>LitigationOrderDocumentService</c>, after a successful download+extraction publish) and
/// <see cref="RecoverStaleWorkAsync"/> (which does check those) to only ever enqueue eligible ids.</summary>
public sealed class LitigationOrderChunkingOrchestrator(
    AppDbContext db, EmbeddingService embeddingService, LitigationOrderChunkingQueue queue,
    ILogger<LitigationOrderChunkingOrchestrator> logger)
{
    public const string ChunkingVersion = "1.0";
    private const int MaxChunkRetryCount = 3;

    public async Task ChunkOrderDocumentAsync(long orderDocumentId, CancellationToken ct)
    {
        var claimed = await db.LitigationOrderDocuments
            .Where(d => d.LitigationOrderDocumentId == orderDocumentId && d.ChunkingStatus == ChunkingStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                .SetProperty(d => d.ChunkingLastAttemptUtc, DateTime.UtcNow), ct);
        if (claimed == 0)
            return; // already claimed/chunked by another worker or a previous run

        var document = await db.LitigationOrderDocuments.Include(d => d.Order).ThenInclude(o => o!.Case)
            .FirstAsync(d => d.LitigationOrderDocumentId == orderDocumentId, ct);

        try
        {
            if (document.Order?.Case is null)
                throw new InvalidOperationException(
                    $"Litigation order document {orderDocumentId} has no reachable LitigationCaseOrder/LitigationCase.");
            if (string.IsNullOrWhiteSpace(document.ExtractedText))
                throw new InvalidOperationException(
                    $"Extracted text not found for litigation order document {orderDocumentId} — nothing to chunk.");

            var textChunks = TextChunker.Chunk(document.ExtractedText, ChatIndexingOptions.Default);
            var embeddings = textChunks.Count > 0
                ? await embeddingService.EmbedDocumentsAsync(textChunks.Select(c => c.Text).ToList(), ct)
                : [];
            // Positional pairing below; a mismatch means the embedding call dropped/duplicated a vector —
            // fail this document (retry, then Failed) rather than persist a misaligned or truncated index.
            if (embeddings.Count != textChunks.Count)
                throw new InvalidOperationException(
                    $"Embedding count {embeddings.Count} does not match chunk count {textChunks.Count} " +
                    $"for litigation order document {orderDocumentId}.");

            var order = document.Order;
            var litigationCase = order.Case!;
            var newChunks = new List<LitigationOrderChunk>(textChunks.Count);
            for (var i = 0; i < textChunks.Count; i++)
            {
                newChunks.Add(new LitigationOrderChunk
                {
                    RequestId = litigationCase.RequestId,
                    LitigationOrderDocumentId = document.LitigationOrderDocumentId,
                    LitigationCaseOrderId = order.LitigationCaseOrderId,
                    LitigationCaseId = litigationCase.LitigationCaseId,
                    CaseNumber = litigationCase.CaseNumber,
                    Cnr = litigationCase.Cnr,
                    Court = litigationCase.Court,
                    OrderType = order.OrderType,
                    OrderDate = order.OrderDate,
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

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.LitigationOrderChunks.Where(c => c.LitigationOrderDocumentId == orderDocumentId).ExecuteDeleteAsync(ct);
            db.LitigationOrderChunks.AddRange(newChunks);
            await db.SaveChangesAsync(ct);
            await db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == orderDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, ChunkingStatus.Chunked)
                    .SetProperty(d => d.ChunkingLastError, (string?)null)
                    .SetProperty(d => d.ChunkingErrorCategory, (string?)null)
                    .SetProperty(d => d.ChunkingFailedUtc, (DateTime?)null), ct);
            await transaction.CommitAsync(ct);

            logger.LogInformation("Litigation order document {Id} chunked: {Count} chunk(s).", orderDocumentId, textChunks.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunking failed for litigation order document {Id}", orderDocumentId);
            var category = DocumentChunkingOrchestrator.ClassifyChunkingError(ex);
            var sanitizedMsg = DocumentChunkingOrchestrator.SanitizeAndCap(ex.Message, 500);
            var retryCount = document.ChunkRetryCount + 1;
            var isTerminal = retryCount >= MaxChunkRetryCount;
            var nextStatus = isTerminal ? ChunkingStatus.Failed : ChunkingStatus.Pending;

            await db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == orderDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, nextStatus)
                    .SetProperty(d => d.ChunkRetryCount, retryCount)
                    .SetProperty(d => d.ChunkingLastError, sanitizedMsg)
                    .SetProperty(d => d.ChunkingErrorCategory, category)
                    .SetProperty(d => d.ChunkingFailedUtc, isTerminal ? (DateTime?)DateTime.UtcNow : null), ct);

            if (!isTerminal)
                queue.Enqueue(orderDocumentId);
        }
    }

    /// <summary>Startup recovery: any document left InProgress by a crash is reset to Pending and re-enqueued
    /// unconditionally (same "fresh process = orphaned" logic as <c>DocumentChunkingOrchestrator</c>'s own
    /// recovery — no staleness timeout at startup). Separately, every Downloaded-and-text-extracted document
    /// still Pending chunking is also swept and re-enqueued, independent of whether it was ever InProgress —
    /// closes the same gap that method's own doc comment describes: a document can reach ChunkingStatus.Pending
    /// (its default) without anything ever having enqueued it, e.g. after a migration backfill or an app crash
    /// between a successful download publish and the chunking enqueue call.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var staleIds = await db.LitigationOrderDocuments
            .Where(d => d.ChunkingStatus == ChunkingStatus.InProgress)
            .Select(d => d.LitigationOrderDocumentId)
            .ToListAsync(ct);

        if (staleIds.Count > 0)
        {
            await db.LitigationOrderDocuments.Where(d => d.ChunkingStatus == ChunkingStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.Pending), ct);
        }

        var pendingEligibleIds = await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Downloaded
                && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.LitigationOrderDocumentId)
            .ToListAsync(ct);

        var idsToEnqueue = staleIds.Concat(pendingEligibleIds).Distinct().ToList();
        foreach (var id in idsToEnqueue)
            queue.Enqueue(id);

        return idsToEnqueue.Count;
    }
}
