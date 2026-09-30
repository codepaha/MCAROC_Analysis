using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

public sealed record OrderOutcomeMatch(
    long LitigationOrderClassificationId, long LitigationOrderDocumentId, long LitigationCaseOrderId, long LitigationCaseId,
    string? CaseNumber, string? Cnr, string? Court, string? OrderDate, string? OrderType,
    IReadOnlyList<LitigationOrderOutcome> Outcomes, decimal? FineAmount, ClassificationConfidence? Confidence,
    bool EvidenceTruncated, bool DocumentDownloaded);

/// <param name="OrdersWithText">Order documents for the request that have retained, chunked text — the population
/// a classification can exist for at all.</param>
/// <param name="OrdersClassified">Of those, how many have a current classification (Completed or
/// InsufficientEvidence). The gap is what a "list every order with X" answer must admit it cannot see.</param>
public sealed record OrderOutcomeLookup(IReadOnlyList<OrderOutcomeMatch> Matches, int OrdersWithText, int OrdersClassified)
{
    public bool IsComplete => OrdersWithText > 0 && OrdersClassified == OrdersWithText;
}

/// <summary>Epic #195's order-outcome lookup: an exact, exhaustive, request-scoped query over persisted
/// <see cref="LitigationOrderClassification"/> rows — never top-K retrieval, so "every order with a fine" is a
/// complete answer over what has been classified, with the coverage gap reported alongside rather than hidden.
/// The current classification for an order is its newest Completed/InsufficientEvidence row; a Failed row never
/// replaces an earlier good one.</summary>
public class LitigationOrderOutcomeQuery(AppDbContext db)
{
    public virtual async Task<OrderOutcomeLookup> FindAsync(long requestId, IReadOnlyCollection<LitigationOrderOutcome> outcomes, CancellationToken ct)
    {
        var ordersWithText = await db.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.RequestId == requestId).Select(c => c.LitigationOrderDocumentId).Distinct().CountAsync(ct);

        var current = (await db.LitigationOrderClassifications.AsNoTracking()
                .Where(c => c.RequestId == requestId
                    && (c.Status == LitigationAiAnalysisItemStatus.Completed || c.Status == LitigationAiAnalysisItemStatus.InsufficientEvidence))
                .ToListAsync(ct))
            .GroupBy(c => c.LitigationOrderDocumentId)
            .Select(g => g.OrderByDescending(c => c.LitigationOrderClassificationId).First())
            .ToList();

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

        return new OrderOutcomeLookup(matches, ordersWithText, current.Count);
    }
}
