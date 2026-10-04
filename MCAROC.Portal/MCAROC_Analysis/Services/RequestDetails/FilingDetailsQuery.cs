using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.RequestDetails;

/// <summary>Only summary counts and citation IDs belong on the initial page.
/// Filing bodies and extraction JSON are loaded by the existing paged Documents endpoint.</summary>
public sealed class FilingDetailsQuery(AppDbContext db)
{
    public async Task LoadAsync(RequestDetailsViewModel vm, CancellationToken ct)
    {
        vm.FilingBatch = await McaFilingBatchResolver.GetAuthoritativeBatchAsync(db, vm.Request.RequestId, ct);
        if (vm.FilingBatch is not { } batch) return;
        var documents = db.McaFilingDocuments.AsNoTracking()
            .Where(d => d.RequestId == vm.Request.RequestId && d.BatchId == batch.BatchId);
        var counts = await documents.Where(d => d.DuplicateOfDocumentId == null)
            .GroupBy(d => new { d.Category, d.TextExtractionMethod })
            .Select(g => new { g.Key.Category, g.Key.TextExtractionMethod, Count = g.Count(),
                Manual = g.Count(d => d.ManualReviewRequired),
                Chunkable = g.Count(d => d.ProcessingStatus == FilingDocumentProcessingStatus.Completed),
                Chunked = g.Count(d => d.ProcessingStatus == FilingDocumentProcessingStatus.Completed && d.ChunkingStatus == ChunkingStatus.Chunked),
                Failed = g.Count(d => d.ProcessingStatus == FilingDocumentProcessingStatus.Completed && d.ChunkingStatus == ChunkingStatus.Failed) })
            .ToListAsync(ct);
        foreach (var group in counts)
        {
            vm.FilingCategoryCounts[group.Category] = vm.FilingCategoryCounts.GetValueOrDefault(group.Category) + group.Count;
            vm.TextExtractionMethodCounts[group.TextExtractionMethod] = vm.TextExtractionMethodCounts.GetValueOrDefault(group.TextExtractionMethod) + group.Count;
            vm.ManualReviewFilingCount += group.Manual;
            vm.AuthoritativeBatchChunkableCount += group.Chunkable;
            vm.AuthoritativeBatchChunkedCount += group.Chunked;
            vm.AuthoritativeBatchChunkingFailedCount += group.Failed;
        }
        var filingIds = db.McaFilings.Where(f => f.BatchId == batch.BatchId && f.RequestId == vm.Request.RequestId).Select(f => f.FilingId);
        var extractionCounts = await db.McaFilingExtractions.AsNoTracking()
            .Where(e => e.FilingId != null && filingIds.Contains(e.FilingId.Value))
            .GroupBy(e => e.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        vm.AiSuccessCount = extractionCounts.Where(g => g.Status == ExtractionStatus.Success).Sum(g => g.Count);
        vm.AiFailedCount = extractionCounts.Where(g => g.Status == ExtractionStatus.Failed).Sum(g => g.Count);
        vm.AuthoritativeBatchTotalChunks = await db.DocumentChunks.CountAsync(c => documents.Select(d => d.FilingDocumentId).Contains(c.FilingDocumentId), ct);
        vm.AuthoritativeDocumentIds = (await documents.Select(d => d.FilingDocumentId).ToListAsync(ct)).ToHashSet();
        vm.ChunkableDocumentCount = vm.AuthoritativeBatchChunkableCount;
        vm.ChunkedDocumentCount = vm.AuthoritativeBatchChunkedCount;
    }
}
