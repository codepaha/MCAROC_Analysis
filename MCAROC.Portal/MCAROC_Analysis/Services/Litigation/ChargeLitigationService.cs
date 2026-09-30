using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Loads a request's open charges and litigation and returns the charge-to-case links
/// (<see cref="ChargeLitigationLinker"/>). The scan reads every case and order text of the request, so the
/// result is cached until the litigation snapshot or the ingestion run changes.</summary>
public sealed class ChargeLitigationService(AppDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    public async Task<ChargeLitigationSummary> GetAsync(long requestId, CancellationToken ct = default)
    {
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);

        // The same snapshot the Litigation tab shows: the newest completed one.
        var snapshotId = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.SearchJob!.RequestId == requestId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.RetrievedUtc)
            .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);

        var openChargeCount = await db.RocCharges.AsNoTracking()
            .CountAsync(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty;

        var key = $"ChargeLitigation:{requestId}:{snapshotId}:{runId}:{openChargeCount}";
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

        var summary = ChargeLitigationLinker.Build(charges, cases, docs, workbook);

        // The app cache is registered with a SizeLimit, so every entry must declare a size (see #331).
        cache.Set(key, summary, new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheFor).SetSize(1));
        return summary;
    }
}
