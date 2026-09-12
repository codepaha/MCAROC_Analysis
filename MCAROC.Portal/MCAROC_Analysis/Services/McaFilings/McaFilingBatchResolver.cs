using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.McaFilings;

public static class McaFilingBatchResolver
{
    /// <summary>
    /// Resolves the single authoritative filing batch for a request, shared across the Details page,
    /// document listing, and document chat retrieval/citations.
    ///
    /// The authoritative batch is the latest completed batch (Completed or CompletedWithErrors)
    /// ordered by completion semantics (CompletedDate ?? StartedDate).
    /// If no batch has completed yet (e.g. during first-time ingestion while unpacking/processing),
    /// falls back to the latest started batch as intentionally partial evidence so in-flight status
    /// can still be rendered, but not treated as verified completed evidence.
    /// </summary>
    public static async Task<McaFilingBatch?> GetAuthoritativeBatchAsync(
        AppDbContext db, long requestId, CancellationToken ct = default)
    {
        var completedBatch = await db.McaFilingBatches
            .Where(b => b.RequestId == requestId
                && (b.Status == FilingBatchStatus.Completed || b.Status == FilingBatchStatus.CompletedWithErrors))
            .OrderByDescending(b => b.CompletedDate ?? b.StartedDate)
            .FirstOrDefaultAsync(ct);

        if (completedBatch is not null)
            return completedBatch;

        // Fallback for first-time ingestion while in-flight or if none completed:
        // Intentionally partial lineage, not completed evidence.
        return await db.McaFilingBatches
            .Where(b => b.RequestId == requestId)
            .OrderByDescending(b => b.StartedDate)
            .FirstOrDefaultAsync(ct);
    }
}
