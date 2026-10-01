using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Loads a request's open charges and litigation and returns the charge-to-case links
/// (<see cref="ChargeLitigationLinker"/>). The scan reads every case and order text of the request, so the
/// result is cached, but only for as long as the compared evidence is unchanged: see <see cref="VersionAsync"/>.</summary>
public sealed class ChargeLitigationService(AppDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    /// <summary>Identifies the evidence a comparison is made from: the newest completed litigation snapshot AND how
    /// many of its order texts have been extracted, and when the latest was. Order text is extracted asynchronously
    /// after a snapshot completes, so the snapshot id alone would let a comparison made while extraction was still
    /// running (and so found nothing) be reused after the matching text arrives. Anything that caches or stores a
    /// result derived from these links (this service, the dossier's memory and disk caches) keys on this value.
    /// Cheap by design: one aggregate over the request's order documents, no text is read.</summary>
    public static async Task<string> VersionAsync(AppDbContext db, long requestId, CancellationToken ct = default)
    {
        var snapshotId = await LatestSnapshotIdAsync(db, requestId, ct);
        if (snapshotId is not { } sid) return "none";

        var caseIds = db.LitigationCaseSourceReports.Where(sr => sr.LitigationReportSnapshotId == sid)
            .Select(sr => sr.LitigationCaseId).Distinct();
        var orderIds = db.LitigationCaseOrders.Where(o => caseIds.Contains(o.LitigationCaseId))
            .Select(o => o.LitigationCaseOrderId);
        var extracted = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => orderIds.Contains(d.LitigationCaseOrderId) && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Last = g.Max(d => d.ExtractedUtc) })
            .FirstOrDefaultAsync(ct);

        return $"{sid}-{extracted?.Count ?? 0}-{extracted?.Last?.Ticks ?? 0}";
    }

    private static Task<long?> LatestSnapshotIdAsync(AppDbContext db, long requestId, CancellationToken ct) =>
        db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.SearchJob!.RequestId == requestId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.RetrievedUtc)
            .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);

    public async Task<ChargeLitigationSummary> GetAsync(long requestId, CancellationToken ct = default)
    {
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);

        // The same snapshot the Litigation tab shows: the newest completed one.
        var snapshotId = await LatestSnapshotIdAsync(db, requestId, ct);
        var version = await VersionAsync(db, requestId, ct);

        var openChargeCount = await db.RocCharges.AsNoTracking()
            .CountAsync(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty with { SnapshotId = snapshotId, Version = version };

        var key = $"ChargeLitigation:{requestId}:{version}:{runId}:{openChargeCount}";
        if (cache.TryGetValue(key, out ChargeLitigationSummary? cached) && cached is not null) return cached;

        var charges = await db.RocCharges.AsNoTracking()
            .Where(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied")
            .Include(c => c.Events)
            .ToListAsync(ct);

        var cases = new List<LitigationCase>();
        var docs = new Dictionary<long, LitigationOrderDocument>();
        if (snapshotId is { } sid)
        {
            var caseIds = db.LitigationCaseSourceReports.Where(sr => sr.LitigationReportSnapshotId == sid)
                .Select(sr => sr.LitigationCaseId).Distinct();
            cases = await db.LitigationCases.AsNoTracking()
                .Where(c => caseIds.Contains(c.LitigationCaseId)).Include(c => c.Orders).ToListAsync(ct);

            var orderIds = db.LitigationCaseOrders.Where(o => caseIds.Contains(o.LitigationCaseId)).Select(o => o.LitigationCaseOrderId);
            var withText = await db.LitigationOrderDocuments.AsNoTracking()
                .Where(d => orderIds.Contains(d.LitigationCaseOrderId) && d.ExtractedText != null)
                .ToListAsync(ct);
            docs = withText.ToDictionary(d => d.LitigationCaseOrderId);
        }

        var workbook = runId is { } rid
            ? await db.Litigations.AsNoTracking().Where(l => l.RequestId == requestId && l.IngestionRunId == rid).ToListAsync(ct)
            : [];

        // Version/SnapshotId let a consumer that caches or stores its own output (the dossier) key on the evidence used.
        var summary = ChargeLitigationLinker.Build(charges, cases, docs, workbook) with { SnapshotId = snapshotId, Version = version };

        // The app cache is registered with a SizeLimit, so every entry must declare a size (see #331).
        cache.Set(key, summary, new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheFor).SetSize(1));
        return summary;
    }
}
