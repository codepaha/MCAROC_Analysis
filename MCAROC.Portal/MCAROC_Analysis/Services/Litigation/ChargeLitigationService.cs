using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Loads a request's open charges and litigation and returns the charge-to-case links
/// (<see cref="ChargeLitigationLinker"/>). The scan reads every case and order text of the request, so the
/// result is cached, but only for as long as the compared evidence is unchanged: see <see cref="VersionAsync"/>.</summary>
public sealed class ChargeLitigationService(AppDbContext db, IMemoryCache cache, LitigationReaderGate? readerGate = null)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(30);

    /// <summary>Identifies the evidence a comparison is made from: the authoritative completed litigation snapshot AND how
    /// many of its order texts have been extracted, and when the latest was. Order text is extracted asynchronously
    /// after a snapshot completes, so the snapshot id alone would let a comparison made while extraction was still
    /// running (and so found nothing) be reused after the matching text arrives. Anything that caches or stores a
    /// result derived from these links (this service, the dossier's memory and disk caches) keys on this value.
    /// Cheap by design: one aggregate over the request's order documents, no text is read.</summary>
    public static async Task<string> VersionAsync(AppDbContext db, long requestId, CancellationToken ct = default)
    {
        var snapshotId = await AuthoritativeSnapshotIdAsync(db, requestId, ct);
        return await SnapshotVersionAsync(db, requestId, snapshotId, ct);
    }

    private static async Task<string> SnapshotVersionAsync(AppDbContext db, long requestId, long? snapshotId, CancellationToken ct)
    {
        // Live court data (portal refreshes, discovery, merges, hides) changes cases without a new snapshot: its version is part of the key.
        var liveVersion = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => (long?)r.LiveDataVersion).FirstOrDefaultAsync(ct);
        if (liveVersion is not { } live) return "none";   // no such request
        if (snapshotId is not { } sid) return $"none-L{live}";

        var caseIds = db.LitigationCaseSourceReports.Where(sr => sr.LitigationReportSnapshotId == sid && sr.Case!.MergedIntoCaseId == null)
            .Select(sr => sr.LitigationCaseId).Distinct();
        var orderIds = db.LitigationCaseOrders.Where(o => caseIds.Contains(o.LitigationCaseId))
            .Select(o => o.LitigationCaseOrderId);
        var extracted = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => orderIds.Contains(d.LitigationCaseOrderId) && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Last = g.Max(d => d.ExtractedUtc) })
            .FirstOrDefaultAsync(ct);

        return $"{sid}-{extracted?.Count ?? 0}-{extracted?.Last?.Ticks ?? 0}-L{live}";
    }

    private static Task<long?> AuthoritativeSnapshotIdAsync(AppDbContext db, long requestId, CancellationToken ct) =>
        LitigationSnapshotSelection.ForRequest(db, requestId)
            .Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);

    /// <summary>Read the initial page's versioned comparison without scanning evidence on pagination.
    /// Null means the comparison must be refreshed through the full request page.</summary>
    public async Task<ChargeLitigationSummary?> GetCachedAsync(long requestId, CancellationToken ct = default)
    {
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);
        var snapshotId = await AuthoritativeSnapshotIdAsync(db, requestId, ct);
        var version = await SnapshotVersionAsync(db, requestId, snapshotId, ct);
        var openChargeCount = await db.RocCharges.AsNoTracking()
            .CountAsync(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty with { SnapshotId = snapshotId, Version = version };
        var source = await SourceAsync(requestId, ct);
        return cache.TryGetValue(CacheKey(requestId, version, runId, openChargeCount, source), out ChargeLitigationSummary? cached) ? cached : null;
    }

    private static string CacheKey(long requestId, string version, long? runId, int openChargeCount, CaseSetSource source)
        => $"ChargeLitigation:{requestId}:{version}:{runId}:{openChargeCount}:{(source == CaseSetSource.Module ? "M" : "L")}";

    private async Task<CaseSetSource> SourceAsync(long requestId, CancellationToken ct) =>
        readerGate is null ? CaseSetSource.Legacy : await readerGate.ResolveChargeLinksAsync(requestId, ct);

    public async Task<ChargeLitigationSummary> GetAsync(long requestId, CancellationToken ct = default)
    {
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);

        // Match the tab's current-report/fallback rule and key the cache on that exact evidence.
        var snapshotId = await AuthoritativeSnapshotIdAsync(db, requestId, ct);
        var version = await SnapshotVersionAsync(db, requestId, snapshotId, ct);

        var openChargeCount = await db.RocCharges.AsNoTracking()
            .CountAsync(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty with { SnapshotId = snapshotId, Version = version };

        var source = await SourceAsync(requestId, ct);
        var key = CacheKey(requestId, version, runId, openChargeCount, source);
        if (cache.TryGetValue(key, out ChargeLitigationSummary? cached) && cached is not null) return cached;

        var charges = await db.RocCharges.AsNoTracking()
            .Where(c => c.RequestId == requestId && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied")
            .Include(c => c.Events)
            .ToListAsync(ct);

        var cases = new List<LitigationCase>();
        var docs = new Dictionary<long, LitigationOrderDocument>();
        if (snapshotId is { } sid)
        {
            // The same definition of "the request's cases" as the register and the case page (plan section 12.6). The Version above stays on the legacy set on
            // purpose: it is the key the dossier caches on, and every live change (refresh, merge, hide, text extraction) bumps the request's live-data version it carries.
            var caseIds = await LitigationCaseSets.ForAsync(db, source, requestId, sid, ct);
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
