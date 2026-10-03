using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

public sealed class McaDocumentRetentionService(
    AppDbContext db,
    IOptions<ReferenceToolOptions> options,
    TimeProvider timeProvider,
    ILogger<McaDocumentRetentionService> logger)
{
    private readonly ReferenceToolOptions _opts = options.Value;

    public async Task<int> PruneExpiredDocumentsAsync(CancellationToken ct = default)
    {
        if (_opts.KeepDocumentsPermanently)
        {
            logger.LogDebug("Document pruning skipped: KeepDocumentsPermanently is true.");
            return 0;
        }

        var retentionDays = _opts.DocumentRetentionDays;
        if (retentionDays <= 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = now.AddDays(-retentionDays);

        var eligibleBatches = await db.McaFilingBatches
            .Include(b => b.Request)
            .Where(b => (b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors)
                        && b.ChargeLinksStamp != null
                        && (b.CompletedDate ?? b.StartedDate) < threshold
                        && b.Request != null
                        && !b.Request.KeepPermanently)
            .Select(b => new { b.BatchId, b.RequestId })
            .ToListAsync(ct);

        if (eligibleBatches.Count == 0)
        {
            return 0;
        }

        var totalPruned = 0;

        foreach (var batch in eligibleBatches)
        {
            try
            {
                var prunedInBatch = await PruneBatchDocumentsAsync(batch.BatchId, batch.RequestId, threshold, now, ct);
                totalPruned += prunedInBatch;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to prune documents for Batch {BatchId} (Request {RequestId}).", batch.BatchId, batch.RequestId);
            }
        }

        return totalPruned;
    }

    private async Task<int> PruneBatchDocumentsAsync(long batchId, long requestId, DateTime threshold, DateTime now, CancellationToken ct)
    {
        var linkedDocIds = await db.ChargeDocumentLinks
            .Where(l => l.BatchId == batchId)
            .Select(l => l.FilingDocumentId)
            .Distinct()
            .ToListAsync(ct);

        var protectedIds = new HashSet<long>(linkedDocIds);

        var chargeCandidates = await db.McaFilingDocuments
            .Where(d => d.BatchId == batchId
                        && (d.Category == FilingCategory.Charge || d.TextExtractionMethod == TextExtractionMethod.Xfa))
            .Select(d => d.FilingDocumentId)
            .ToListAsync(ct);

        foreach (var id in chargeCandidates)
        {
            protectedIds.Add(id);
        }

        var canonicalsOfProtected = await db.McaFilingDocuments
            .Where(d => d.BatchId == batchId && protectedIds.Contains(d.FilingDocumentId) && d.DuplicateOfDocumentId != null)
            .Select(d => d.DuplicateOfDocumentId!.Value)
            .Distinct()
            .ToListAsync(ct);

        foreach (var canId in canonicalsOfProtected)
        {
            protectedIds.Add(canId);
        }

        var candidates = await db.McaFilingDocuments
            .Where(d => d.BatchId == batchId
                        && d.RetiredUtc == null
                        && d.UpdatedAt < threshold
                        && !string.IsNullOrEmpty(d.StoragePath)
                        && d.SourceAwsPath != null
                        && !protectedIds.Contains(d.FilingDocumentId)
                        && (d.DuplicateOfDocumentId == null || !protectedIds.Contains(d.DuplicateOfDocumentId.Value)))
            .ToListAsync(ct);

        var prunedCount = 0;

        foreach (var doc in candidates)
        {
            var path = doc.StoragePath;
            var fileSuccessfullyRemoved = false;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                if (File.GetLastWriteTimeUtc(path) >= threshold)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                    fileSuccessfullyRemoved = !File.Exists(path);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete physical file {Path} for Doc {DocId}", path, doc.FilingDocumentId);
                    fileSuccessfullyRemoved = false;
                }
            }
            else
            {
                fileSuccessfullyRemoved = true;
            }

            if (fileSuccessfullyRemoved)
            {
                doc.StoragePath = string.Empty;
                doc.RetiredUtc = now;
                doc.UpdatedAt = now;
                prunedCount++;
            }
        }

        if (prunedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Pruned {Count} expired filing PDFs for Batch {BatchId} (Request {RequestId}).", prunedCount, batchId, requestId);
        }

        return prunedCount;
    }

    public async Task<int> PruneSupersededBatchChunksAsync(CancellationToken ct = default)
    {
        var graceDays = _opts.SupersededBatchGraceDays;
        if (graceDays <= 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var threshold = now.AddDays(-graceDays);

        var requestIdsWithBatches = await db.McaFilingBatches
            .Where(b => (b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors))
            .Select(b => b.RequestId)
            .Distinct()
            .ToListAsync(ct);

        var totalChunksDeleted = 0;

        foreach (var reqId in requestIdsWithBatches)
        {
            var authoritativeBatch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, reqId, ct);
            if (authoritativeBatch == null || authoritativeBatch.Status is not (FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors))
            {
                continue;
            }

            var authorEffectiveDate = authoritativeBatch.CompletedDate ?? authoritativeBatch.StartedDate;

            var supersededBatchIds = await db.McaFilingBatches
                .Where(b => b.RequestId == reqId
                            && b.BatchId != authoritativeBatch.BatchId
                            && (b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors)
                            && (b.CompletedDate ?? b.StartedDate) <= authorEffectiveDate
                            && (b.CompletedDate ?? b.StartedDate) < threshold)
                .Select(b => b.BatchId)
                .ToListAsync(ct);

            if (supersededBatchIds.Count == 0) continue;

            var deleted = await db.DocumentChunks
                .Where(c => supersededBatchIds.Contains(c.BatchId))
                .ExecuteDeleteAsync(ct);

            if (deleted > 0)
            {
                totalChunksDeleted += deleted;
                logger.LogInformation("Deleted {Count} superseded document chunks for Request {RequestId} (Batches: {Batches})",
                    deleted, reqId, string.Join(", ", supersededBatchIds));
            }
        }

        return totalChunksDeleted;
    }
}