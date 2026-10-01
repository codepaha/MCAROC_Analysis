using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.LitigationData;

public sealed record OrderOutcomeMatch(
    long LitigationOrderClassificationId, long LitigationOrderDocumentId, long LitigationCaseOrderId, long LitigationCaseId,
    string? CaseNumber, string? Cnr, string? Court, string? OrderDate, string? OrderType,
    IReadOnlyList<LitigationOrderOutcome> Outcomes, decimal? FineAmount, ClassificationConfidence? Confidence,
    bool EvidenceTruncated, bool DocumentDownloaded);

/// <param name="OrdersWithText">Order documents for the request that have retained, chunked text — the population
/// a classification can exist for at all.</param>
/// <param name="OrdersClassified">Of those, how many have a classification (Completed or InsufficientEvidence) of their
/// <em>current</em> text. The gap is what a "list every order with X" answer must admit it cannot see.</param>
/// <param name="OrdersOutdated">Of the unclassified ones, how many were classified before but their text has changed
/// since. Their earlier outcomes are kept for audit but never returned as current.</param>
/// <param name="CurrentStatusByOrderId">Per LitigationCaseOrderId, the status of its current classification (Completed or
/// InsufficientEvidence; Completed wins when an order has several documents). Matches only carry orders WITH a wanted
/// outcome, so a view needs this to tell "classified, outcome not determinable" apart from "not classified". Null means none.</param>
public sealed record OrderOutcomeLookup(IReadOnlyList<OrderOutcomeMatch> Matches, int OrdersWithText, int OrdersClassified, int OrdersOutdated = 0,
    IReadOnlyDictionary<long, LitigationAiAnalysisItemStatus>? CurrentStatusByOrderId = null)
{
    public static OrderOutcomeLookup Empty { get; } = new([], 0, 0);

    public bool IsComplete => OrdersWithText > 0 && OrdersClassified == OrdersWithText;
}

/// <summary>Epic #195's order-outcome lookup: an exact, exhaustive, request-scoped query over persisted
/// <see cref="LitigationOrderClassification"/> rows — never top-K retrieval, so "every order with a fine" is a
/// complete answer over what has been classified, with the coverage gap reported alongside rather than hidden.
///
/// "Current" is decided against the order's text as it is now, not by recency: a classification counts only when its
/// evidence and prompt hashes match what <see cref="LitigationOrderClassifier"/> would build from the order's current
/// chunks. An order whose text changed after it was classified therefore has no current outcome (and coverage is
/// incomplete) until a new run classifies it — a failed reclassification never lets the old outcome stand in. Among
/// matching rows the newest Completed/InsufficientEvidence one wins; a Failed row never counts.
///
/// Recomputing the hashes reads every order's text, and the Litigation tab runs this on each Details page load, so the
/// hashes are cached (when a cache is supplied) under the request's chunk version — chunks are only ever deleted and
/// re-inserted, never edited, so their count and highest id change whenever any order's text does. A request with no
/// successful classification skips the text entirely.</summary>
public class LitigationOrderOutcomeQuery(AppDbContext db, IMemoryCache? cache = null)
{
    private static readonly TimeSpan HashCacheFor = TimeSpan.FromMinutes(30);

    public virtual async Task<OrderOutcomeLookup> FindAsync(long requestId, IReadOnlyCollection<LitigationOrderOutcome> outcomes, CancellationToken ct)
    {
        var chunkVersion = await db.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.RequestId == requestId)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), MaxId = g.Max(c => c.LitigationOrderChunkId), Documents = g.Select(c => c.LitigationOrderDocumentId).Distinct().Count() })
            .FirstOrDefaultAsync(ct);
        if (chunkVersion is null)
            return OrderOutcomeLookup.Empty;

        var successful = await db.LitigationOrderClassifications.AsNoTracking()
            .Where(c => c.RequestId == requestId
                && (c.Status == LitigationAiAnalysisItemStatus.Completed || c.Status == LitigationAiAnalysisItemStatus.InsufficientEvidence))
            .ToListAsync(ct);
        if (successful.Count == 0)
            return new OrderOutcomeLookup([], chunkVersion.Documents, 0);

        var hashKey = $"LitigationOrderEvidenceHashes:{requestId}:{chunkVersion.Count}:{chunkVersion.MaxId}";
        if (cache is null || !cache.TryGetValue(hashKey, out Dictionary<long, (string EvidenceHash, string PromptHash)>? currentHashes) || currentHashes is null)
        {
            currentHashes = await ComputeCurrentHashesAsync(requestId, ct);
            cache?.Set(hashKey, currentHashes, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = HashCacheFor });
        }

        var current = successful
            .Where(c => currentHashes.TryGetValue(c.LitigationOrderDocumentId, out var h) && c.EvidenceHash == h.EvidenceHash && c.PromptHash == h.PromptHash)
            .GroupBy(c => c.LitigationOrderDocumentId)
            .Select(g => g.OrderByDescending(c => c.LitigationOrderClassificationId).First())
            .ToList();
        var currentDocuments = current.Select(c => c.LitigationOrderDocumentId).ToHashSet();
        var outdated = successful.Select(c => c.LitigationOrderDocumentId).Distinct()
            .Count(d => currentHashes.ContainsKey(d) && !currentDocuments.Contains(d));

        var currentStatusByOrderId = current.GroupBy(c => c.LitigationCaseOrderId)
            .ToDictionary(g => g.Key, g => g.Any(c => c.Status == LitigationAiAnalysisItemStatus.Completed)
                ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.InsufficientEvidence);

        var wanted = outcomes.ToHashSet();
        var matching = current
            .Where(c => c.Status == LitigationAiAnalysisItemStatus.Completed)
            .Select(c => (Row: c, Outcomes: LitigationOrderClassifier.ParseOutcomeTypes(c.OutcomeTypesJson)))
            .Where(x => x.Outcomes.Any(wanted.Contains))
            .ToList();

        var orderIds = matching.Select(x => x.Row.LitigationCaseOrderId).ToList();
        var details = await db.LitigationCaseOrders.AsNoTracking()
            .Where(o => orderIds.Contains(o.LitigationCaseOrderId))
            .Select(o => new { o.LitigationCaseOrderId, o.OrderDate, o.OrderType, o.Case!.CaseNumber, o.Case.Cnr, o.Case.Court })
            .ToDictionaryAsync(o => o.LitigationCaseOrderId, ct);
        var documentIds = matching.Select(x => x.Row.LitigationOrderDocumentId).ToList();
        var downloaded = (await db.LitigationOrderDocuments.AsNoTracking()
                .Where(d => documentIds.Contains(d.LitigationOrderDocumentId) && d.Status == LitigationOrderDocumentStatus.Downloaded)
                .Select(d => d.LitigationOrderDocumentId).ToListAsync(ct))
            .ToHashSet();

        var matches = matching
            .Select(x =>
            {
                var d = details.GetValueOrDefault(x.Row.LitigationCaseOrderId);
                return new OrderOutcomeMatch(x.Row.LitigationOrderClassificationId, x.Row.LitigationOrderDocumentId,
                    x.Row.LitigationCaseOrderId, x.Row.LitigationCaseId, d?.CaseNumber, d?.Cnr, d?.Court, d?.OrderDate, d?.OrderType,
                    x.Outcomes, x.Row.FineAmount, x.Row.Confidence, x.Row.EvidenceTruncated,
                    downloaded.Contains(x.Row.LitigationOrderDocumentId));
            })
            .OrderBy(m => m.CaseNumber ?? m.Cnr).ThenBy(m => m.LitigationCaseOrderId)
            .ToList();

        return new OrderOutcomeLookup(matches, currentHashes.Count, current.Count, outdated, currentStatusByOrderId);
    }

    private async Task<Dictionary<long, (string EvidenceHash, string PromptHash)>> ComputeCurrentHashesAsync(long requestId, CancellationToken ct)
    {
        // Only the columns evidence is built from — never the embedding vector.
        var chunks = await db.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.RequestId == requestId)
            .Select(c => new LitigationOrderChunk
            {
                LitigationOrderDocumentId = c.LitigationOrderDocumentId, LitigationCaseOrderId = c.LitigationCaseOrderId,
                LitigationCaseId = c.LitigationCaseId, CaseNumber = c.CaseNumber, Court = c.Court, OrderDate = c.OrderDate,
                OrderType = c.OrderType, PageNumber = c.PageNumber, ChunkIndex = c.ChunkIndex, ChunkText = c.ChunkText
            })
            .ToListAsync(ct);
        return chunks.GroupBy(c => c.LitigationOrderDocumentId)
            .ToDictionary(g => g.Key, g => LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence(g.ToList())));
    }
}
