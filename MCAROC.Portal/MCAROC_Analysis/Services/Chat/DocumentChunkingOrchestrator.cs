using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Chunks and embeds one McaFilingDocument at a time. Mirrors Phase 2/3's atomic-claim pattern
/// exactly. Rollback-safe re-chunk: chunking and embedding (the parts that can fail) happen entirely before
/// touching the database — only once the full new chunk set is computed does one transaction delete the
/// document's existing chunks and insert the new set together, so a failure never destroys a previously
/// working index.</summary>
public partial class DocumentChunkingOrchestrator(AppDbContext db, EmbeddingService embeddingService, DocumentChunkingQueue queue, ILogger<DocumentChunkingOrchestrator> logger)
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
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                .SetProperty(d => d.ChunkingLastAttemptUtc, DateTime.UtcNow), ct);
        if (claimed == 0)
            return; // already claimed/chunked by another worker or a previous run

        var document = await db.McaFilingDocuments.Include(d => d.Filing).FirstAsync(d => d.FilingDocumentId == filingDocumentId, ct);

        try
        {
            if (document.ExtractedTextPath is null || !File.Exists(document.ExtractedTextPath))
                throw new InvalidOperationException($"Extracted text file not found for document {filingDocumentId} at '{document.ExtractedTextPath}'.");

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
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, ChunkingStatus.Chunked)
                    .SetProperty(d => d.ChunkingLastError, (string?)null)
                    .SetProperty(d => d.ChunkingErrorCategory, (string?)null)
                    .SetProperty(d => d.ChunkingFailedUtc, (DateTime?)null), ct);
            await transaction.CommitAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunking failed for document {DocumentId}", filingDocumentId);
            var category = ClassifyChunkingError(ex);
            var sanitizedMsg = SanitizeAndCap(ex.Message, 500);
            var retryCount = document.ChunkRetryCount + 1;
            var isTerminal = retryCount >= MaxChunkRetryCount;
            var nextStatus = isTerminal ? ChunkingStatus.Failed : ChunkingStatus.Pending;

            await db.McaFilingDocuments.Where(d => d.FilingDocumentId == filingDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.ChunkingStatus, nextStatus)
                    .SetProperty(d => d.ChunkRetryCount, retryCount)
                    .SetProperty(d => d.ChunkingLastError, sanitizedMsg)
                    .SetProperty(d => d.ChunkingErrorCategory, category)
                    .SetProperty(d => d.ChunkingFailedUtc, isTerminal ? (DateTime?)DateTime.UtcNow : null), ct);

            if (!isTerminal)
                queue.Enqueue(document.BatchId);
        }
    }

    public async Task<bool> RetryFailedDocumentAsync(long filingDocumentId, long expectedBatchId, CancellationToken ct)
    {
        var rows = await db.McaFilingDocuments
            .Where(d => d.FilingDocumentId == filingDocumentId
                     && d.BatchId == expectedBatchId
                     && d.DuplicateOfDocumentId == null
                     && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
                     && d.ChunkingStatus == ChunkingStatus.Failed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.Pending)
                .SetProperty(d => d.ChunkRetryCount, 0)
                .SetProperty(d => d.ChunkingLastError, (string?)null)
                .SetProperty(d => d.ChunkingErrorCategory, (string?)null)
                .SetProperty(d => d.ChunkingFailedUtc, (DateTime?)null), ct);

        if (rows == 1)
            queue.Enqueue(expectedBatchId);

        return rows == 1;
    }

    public static string ClassifyChunkingError(Exception ex) => ex switch
    {
        HttpRequestException => "EmbeddingApi",
        InvalidOperationException e when e.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) => "TextFileMissing",
        InvalidOperationException e when e.Message.Contains("Embedding count", StringComparison.OrdinalIgnoreCase) => "VectorMismatch",
        DbUpdateException => "DatabaseWrite",
        _ => "Unknown"
    };

    public static string SanitizeAndCap(string? message, int maxChars = 500)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown error";

        var text = message;
        // 1. Redact Bearer and Basic auth tokens
        text = BearerTokenPattern().Replace(text, "Bearer [REDACTED]");
        text = BasicAuthPattern().Replace(text, "Basic [REDACTED]");

        // 2. Redact key / api_key / token / password / secret assignments
        text = SecretAssignmentPattern().Replace(text, "$1=[REDACTED]");

        // 3. Redact database connection strings
        text = ConnectionStringPattern().Replace(text, "[CONNECTION_STRING_REDACTED]");

        // 4. Redact Windows drive paths (e.g. C:\path\file.ext), UNC paths (\\server\share\file.ext), and URI file paths
        text = WindowsDrivePathPattern().Replace(text, "[PATH_REDACTED]");
        text = UncPathPattern().Replace(text, "[PATH_REDACTED]");
        text = UriFilePathPattern().Replace(text, "[PATH_REDACTED]");

        // 5. Redact PAN numbers (5 uppercase letters, 4 digits, 1 uppercase letter)
        text = PanPattern().Replace(text, "[PAN_REDACTED]");

        // 6. Strip stack trace lines starting with "at ..."
        text = StackTracePattern().Replace(text, "");

        // 7. Collapse multi-whitespace and trim
        text = MultiWhitespacePattern().Replace(text, " ").Trim();

        return text.Length <= maxChars ? text : text[..maxChars];
    }

    [GeneratedRegex(@"\bBearer\s+[A-Za-z0-9_\-\.]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerTokenPattern();

    [GeneratedRegex(@"\bBasic\s+[A-Za-z0-9+/=]+", RegexOptions.IgnoreCase)]
    private static partial Regex BasicAuthPattern();

    [GeneratedRegex(@"(?i)\b(key|api[-_]?key|token|password|secret|pwd)\s*[=:]\s*['""]?[^\s&""';]+['""]?")]
    private static partial Regex SecretAssignmentPattern();

    [GeneratedRegex(@"(?i)\b(?:Server|Data Source|User ID|Initial Catalog)\s*=[^;]+(?:;|$)")]
    private static partial Regex ConnectionStringPattern();

    [GeneratedRegex(@"[A-Za-z]:\\(?:[^\r\n\t""':;<>|?*,]+\\)*[^\r\n\t""':;<>|?*,]+")]
    private static partial Regex WindowsDrivePathPattern();

    [GeneratedRegex(@"\\\\[^\r\n\t""':;<>|?*,]+")]
    private static partial Regex UncPathPattern();

    [GeneratedRegex(@"(?:file:\/\/\/|\/)[a-zA-Z0-9_\-.\/]+\.[a-zA-Z0-9]+")]
    private static partial Regex UriFilePathPattern();

    [GeneratedRegex(@"\b[A-Z]{5}[0-9]{4}[A-Z]\b")]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"^\s*at\s+.*$", RegexOptions.Multiline)]
    private static partial Regex StackTracePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiWhitespacePattern();

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
