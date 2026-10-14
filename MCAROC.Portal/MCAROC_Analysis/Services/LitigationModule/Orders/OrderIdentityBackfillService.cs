using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>Idempotent backfill service for litigation order identity, versioning and cross-source equivalence (plan section 4.5, #440).</summary>
public sealed class OrderIdentityBackfillService(
    AppDbContext db,
    ILogger<OrderIdentityBackfillService> logger)
{
    /// <summary>Executes the backfill idempotently. Safe to re-run, merges nothing it cannot prove.</summary>
    public async Task<int> BackfillAsync(long? caseId = null, CancellationToken ct = default)
    {
        var modifiedCount = 0;

        // 1. Backfill LitigationCaseOrders missing Source, OrderKey, SourceLocator, or Version
        var ordersQuery = db.LitigationCaseOrders
            .Where(o => o.Source == null || o.OrderKey == null || o.SourceLocator == null || o.Version <= 0);
        if (caseId.HasValue)
        {
            ordersQuery = ordersQuery.Where(o => o.LitigationCaseId == caseId.Value);
        }
        var ordersToBackfill = await ordersQuery.ToListAsync(ct);

        foreach (var order in ordersToBackfill)
        {
            var changed = false;
            if (order.Source == null)
            {
                order.Source = "Bpr";
                changed = true;
            }
            if (order.Version <= 0)
            {
                order.Version = 1;
                changed = true;
            }

            if (order.OrderKey == null)
            {
                if (!string.IsNullOrWhiteSpace(order.PdfUrl) && !string.IsNullOrWhiteSpace(order.OrderDate) && !string.IsNullOrWhiteSpace(order.OrderType))
                {
                    order.OrderKey = OrderKeyBuilder.ForBpr(order.PdfUrl, order.OrderDate, order.OrderType);
                }
                else
                {
                    order.OrderKey = $"{(order.PdfUrl ?? "").Trim()}|{(order.OrderDate ?? "").Trim()}|{(order.OrderType ?? "").Trim()}";
                }

                if (order.OrderKey != null && order.OrderKey.Length > 800)
                {
                    order.OrderKey = order.OrderKey[..800];
                }
                changed = true;
            }

            if (order.SourceLocator == null)
            {
                order.SourceLocator = JsonSerializer.Serialize(new OrderSourceLocator(
                    Source: order.Source,
                    PdfUrl: order.PdfUrl,
                    OrderType: order.OrderType,
                    OrderDate: order.OrderDate));
                changed = true;
            }

            if (changed)
            {
                modifiedCount++;
            }
        }

        if (ordersToBackfill.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // 2. Backfill LitigationOrderDocuments missing Version or ContentHash
        var docsQuery = db.LitigationOrderDocuments
            .Include(d => d.Order)
            .Where(d => d.Version <= 0 || (d.ContentHash == null && d.Status == LitigationOrderDocumentStatus.Downloaded));
        if (caseId.HasValue)
        {
            docsQuery = docsQuery.Where(d => db.LitigationCaseOrders.Any(o => o.LitigationCaseOrderId == d.LitigationCaseOrderId && o.LitigationCaseId == caseId.Value));
        }
        var docsToBackfill = await docsQuery.ToListAsync(ct);

        foreach (var doc in docsToBackfill)
        {
            var changed = false;
            if (doc.Version <= 0)
            {
                doc.Version = 1;
                changed = true;
            }

            if (doc.ContentHash == null && doc.Status == LitigationOrderDocumentStatus.Downloaded)
            {
                if (doc.TextExtractionMethod == TextExtractionMethod.Html && !string.IsNullOrWhiteSpace(doc.ExtractedText))
                {
                    doc.ContentHash = OrderContentHasher.ComputeHtmlTextHash(doc.ExtractedText);
                    changed = true;
                }
                else if (!string.IsNullOrWhiteSpace(doc.FileHash))
                {
                    doc.ContentHash = doc.FileHash;
                    changed = true;
                }
                else if (!string.IsNullOrWhiteSpace(doc.StoragePath) && File.Exists(doc.StoragePath))
                {
                    try
                    {
                        var bytes = File.ReadAllBytes(doc.StoragePath);
                        var hash = OrderContentHasher.ComputeBytesHash(bytes);
                        doc.FileHash = hash;
                        doc.ContentHash = hash;
                        changed = true;
                    }
                    catch
                    {
                        // Leave hash unknown
                    }
                }
                // When content is unknown, leave doc.ContentHash as null (unknown) — never fabricate an empty-byte hash.
            }

            if (doc.Order != null && doc.Order.ContentHash == null && doc.ContentHash != null)
            {
                doc.Order.ContentHash = doc.ContentHash;
                changed = true;
            }

            if (changed)
            {
                modifiedCount++;
            }
        }

        if (docsToBackfill.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        // 3. Evaluate cross-source equivalence and collapse duplicates on verified cases
        var candidateGroupsQuery = db.LitigationCaseOrders
            .Where(o => o.CollapsedIntoOrderId == null && !o.IsSuperseded && o.ContentHash != null && o.OrderDate != null);
        if (caseId.HasValue)
        {
            candidateGroupsQuery = candidateGroupsQuery.Where(o => o.LitigationCaseId == caseId.Value);
        }
        var candidateGroups = await candidateGroupsQuery
            .GroupBy(o => new { o.LitigationCaseId, o.OrderDate })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.LitigationCaseId, g.Key.OrderDate })
            .ToListAsync(ct);

        foreach (var group in candidateGroups)
        {
            var targetCaseId = group.LitigationCaseId;
            var caseEntity = await db.LitigationCases.AsNoTracking().FirstOrDefaultAsync(c => c.LitigationCaseId == targetCaseId, ct);
            if (caseEntity == null) continue;

            var isVerified = await IsCaseVerifiedAsync(caseEntity.RequestId, targetCaseId, ct);
            if (!isVerified) continue;

            var groupOrders = await db.LitigationCaseOrders
                .Where(o => o.LitigationCaseId == targetCaseId && o.OrderDate == group.OrderDate && o.CollapsedIntoOrderId == null && !o.IsSuperseded && o.ContentHash != null)
                .ToListAsync(ct);

            for (var i = 0; i < groupOrders.Count; i++)
            {
                var a = groupOrders[i];
                if (a.CollapsedIntoOrderId != null || a.IsSuperseded) continue;

                var aDoc = await db.LitigationOrderDocuments.AsNoTracking()
                    .Where(d => d.LitigationCaseOrderId == a.LitigationCaseOrderId && !d.IsSuperseded)
                    .OrderByDescending(d => d.Version)
                    .FirstOrDefaultAsync(ct);

                var aHasText = aDoc?.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted && !string.IsNullOrWhiteSpace(aDoc.ExtractedText);
                var aItem = new OrderComparisonItem(
                    OrderId: a.LitigationCaseOrderId,
                    Source: a.Source ?? "Bpr",
                    OrderKey: a.OrderKey ?? "",
                    CaseId: targetCaseId,
                    IsCaseVerified: true,
                    OrderDate: CaseDates.ParseDate(a.OrderDate),
                    ContentHash: a.ContentHash,
                    HasExtractedText: aHasText);

                for (var j = i + 1; j < groupOrders.Count; j++)
                {
                    var b = groupOrders[j];
                    if (b.CollapsedIntoOrderId != null || b.IsSuperseded) continue;

                    var bDoc = await db.LitigationOrderDocuments.AsNoTracking()
                        .Where(d => d.LitigationCaseOrderId == b.LitigationCaseOrderId && !d.IsSuperseded)
                        .OrderByDescending(d => d.Version)
                        .FirstOrDefaultAsync(ct);

                    var bHasText = bDoc?.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted && !string.IsNullOrWhiteSpace(bDoc.ExtractedText);
                    var bItem = new OrderComparisonItem(
                        OrderId: b.LitigationCaseOrderId,
                        Source: b.Source ?? "Bpr",
                        OrderKey: b.OrderKey ?? "",
                        CaseId: targetCaseId,
                        IsCaseVerified: true,
                        OrderDate: CaseDates.ParseDate(b.OrderDate),
                        ContentHash: b.ContentHash,
                        HasExtractedText: bHasText);

                    var eq = OrderEquivalence.Evaluate(aItem, bItem);
                    if (eq.IsEquivalent && eq.SurvivorOrderId.HasValue && eq.CollapsedOrderId.HasValue)
                    {
                        var survivorId = eq.SurvivorOrderId.Value;
                        var collapsedId = eq.CollapsedOrderId.Value;

                        await db.LitigationCaseOrders
                            .Where(o => o.LitigationCaseOrderId == collapsedId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(o => o.CollapsedIntoOrderId, survivorId)
                                .SetProperty(o => o.IsSuperseded, true), ct);

                        await db.LitigationOrderDocuments
                            .Where(d => d.LitigationCaseOrderId == collapsedId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(d => d.IsSuperseded, true), ct);

                        await db.LitigationOrderChunks
                            .Where(c => c.LitigationCaseOrderId == collapsedId)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(c => c.IsSuperseded, true), ct);

                        logger.LogInformation(
                            "OrderIdentityBackfill: Collapsed order {CollapsedId} into {SurvivorId} ({Reason})",
                            collapsedId, survivorId, eq.Reason);

                        modifiedCount++;
                    }
                }
            }
        }

        return modifiedCount;
    }

    private async Task<bool> IsCaseVerifiedAsync(long requestId, long caseId, CancellationToken ct)
    {
        var module = await db.Set<ModuleCase>().AsNoTracking()
            .Where(m => m.LegacyLitigationCaseId == caseId)
            .Select(m => new { m.CaseId })
            .FirstOrDefaultAsync(ct);

        if (module is not null)
        {
            return await db.Set<ModuleRequestLink>().AsNoTracking()
                .AnyAsync(l => l.RequestId == requestId && l.CaseId == module.CaseId && l.Verdict == (int)PartyMatchOutcome.Verified, ct);
        }

        return await db.LitigationCases.AsNoTracking()
            .AnyAsync(c => c.LitigationCaseId == caseId && c.RequestId == requestId && c.MergedIntoCaseId == null, ct);
    }
}

