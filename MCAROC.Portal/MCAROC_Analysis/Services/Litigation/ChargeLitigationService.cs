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

        // Order text is counted over every case either source can compare: the current snapshot's cases and the request's retained set (any completed snapshot). The module reads
        // the retained set, so text extracted for a case only an older snapshot lists must move the version too; one definition for both modes keeps the dossier's key identical.
        var caseIds = db.LitigationCaseSourceReports
            .Where(sr => sr.Case!.MergedIntoCaseId == null && (sr.LitigationReportSnapshotId == sid
                || (sr.ReportSnapshot!.RequestId == requestId && sr.ReportSnapshot.Status == LitigationReportSnapshotStatus.Completed)))
            .Select(sr => sr.LitigationCaseId).Distinct();
        var orderIds = db.LitigationCaseOrders.Where(o => caseIds.Contains(o.LitigationCaseId))
            .Select(o => o.LitigationCaseOrderId);
        var extracted = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => orderIds.Contains(d.LitigationCaseOrderId) && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted)
            .GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Last = g.Max(d => d.ExtractedUtc) })
            .FirstOrDefaultAsync(ct);

        // #554: a property link comes from the order's reading and the charge's property extraction, both made after the text: either finishing moves it.
        var read = await db.LitigationOrderPropertyReadings.AsNoTracking()
            .Where(r => r.RequestId == requestId && r.CompletedUtc != null)
            .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Last = g.Max(r => r.CompletedUtc) }).FirstOrDefaultAsync(ct);
        var properties = await MCAROC_Analysis.Services.PropertyParticulars.PropertyParticularsExtractionService.VersionAsync(db, requestId, ct);

        return $"{sid}-{extracted?.Count ?? 0}-{extracted?.Last?.Ticks ?? 0}-L{live}-R{read?.Count ?? 0}-{read?.Last?.Ticks ?? 0}-{properties}";
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
            .CountAsync(c => c.RequestId == requestId && (runId == null || c.IngestionRunId == runId) && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty with { SnapshotId = snapshotId, Version = version };
        var source = await SourceAsync(requestId, ct);
        return cache.TryGetValue(CacheKey(requestId, version, runId, openChargeCount, source), out ChargeLitigationSummary? cached) ? cached : null;
    }

    private static string CacheKey(long requestId, string version, long? runId, int openChargeCount, CaseSetSource source)
        => $"ChargeLitigation:{requestId}:{version}:{runId}:{openChargeCount}:{(source == CaseSetSource.Module ? "M" : "L")}";

    private async Task<CaseSetSource> SourceAsync(long requestId, CancellationToken ct) =>
        readerGate is null ? CaseSetSource.Legacy : await readerGate.ResolveChargeLinksAsync(requestId, ct);

    /// <summary>The company's registered, former and added names (<see cref="LitigationCompanyNames"/>): what decides its side of a case.</summary>
    public static Task<List<string>> CompanyNamesAsync(AppDbContext db, long requestId, CancellationToken ct) =>
        LitigationCompanyNames.ForRequestAsync(db, requestId, ct);

    /// <summary>The company's open charges as of its latest completed ingestion. Each ingestion stores its own charge rows, so without the run
    /// every charge would be compared once per ingestion.</summary>
    public static Task<List<RocCharge>> OpenChargesAsync(AppDbContext db, long requestId, long? runId, CancellationToken ct) =>
        db.RocCharges.AsNoTracking()
            .Where(c => c.RequestId == requestId && (runId == null || c.IngestionRunId == runId) && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied")
            .Include(c => c.Events)
            .ToListAsync(ct);

    /// <summary>What the property links are made from (#554): the properties each case's orders name (from their readings) and the open charges'
    /// immovable properties (from the charge extraction, else the rules reading).</summary>
    public static async Task<(IReadOnlyDictionary<long, MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties> OrderProperties,
        IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.ChargedProperty> Charged)> PropertyEvidenceAsync(
        AppDbContext db, IReadOnlyList<RocCharge> charges, IReadOnlyList<LitigationCase> cases, IReadOnlyDictionary<long, LitigationOrderDocument> docsByOrderId,
        CancellationToken ct)
    {
        var extractions = await MCAROC_Analysis.Services.PropertyParticulars.PropertyParticularsExtractionService.LoadCompletedAsync(db, charges.SelectMany(c => c.Events), ct);
        var charged = MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyChargeMatcher.ChargedProperties(charges, extractions);
        var byCase = new Dictionary<long, MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties>();
        if (charged.Count == 0 || cases.Count == 0) return (byCase, charged);
        var documentIds = docsByOrderId.Values.Select(d => d.LitigationOrderDocumentId).ToList();
        var readings = new Dictionary<long, LitigationOrderPropertyReading>();
        foreach (var chunk in documentIds.Chunk(2000))
            foreach (var r in await db.LitigationOrderPropertyReadings.AsNoTracking().Where(r => chunk.Contains(r.LitigationOrderDocumentId)).ToListAsync(ct))
                readings[r.LitigationOrderDocumentId] = r;
        foreach (var c in cases)
        {
            var orders = c.Orders.Where(o => o.CollapsedIntoOrderId == null && !o.IsSuperseded).OrderBy(o => o.LitigationCaseOrderId)
                .Where(o => docsByOrderId.TryGetValue(o.LitigationCaseOrderId, out var d) && d.Status == LitigationOrderDocumentStatus.Downloaded)
                .Select(o => (o, docsByOrderId[o.LitigationCaseOrderId])).ToList();
            if (orders.Count > 0) byCase[c.LitigationCaseId] = MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties.Build(orders, readings);
        }
        return (byCase, charged);
    }

    public async Task<ChargeLitigationSummary> GetAsync(long requestId, CancellationToken ct = default)
    {
        var runId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => r.LatestCompletedIngestionRunId).FirstOrDefaultAsync(ct);

        // Match the tab's current-report/fallback rule and key the cache on that exact evidence.
        var snapshotId = await AuthoritativeSnapshotIdAsync(db, requestId, ct);
        var version = await SnapshotVersionAsync(db, requestId, snapshotId, ct);

        var openChargeCount = await db.RocCharges.AsNoTracking()
            .CountAsync(c => c.RequestId == requestId && (runId == null || c.IngestionRunId == runId) && c.SatisfactionDate == null && c.ChargeStatus != "Satisfied", ct);
        if (openChargeCount == 0) return ChargeLitigationSummary.Empty with { SnapshotId = snapshotId, Version = version };

        var source = await SourceAsync(requestId, ct);
        var key = CacheKey(requestId, version, runId, openChargeCount, source);
        if (cache.TryGetValue(key, out ChargeLitigationSummary? cached) && cached is not null) return cached;

        var charges = await OpenChargesAsync(db, requestId, runId, ct);

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

        // Version/SnapshotId let a consumer that caches or stores its own output (the dossier) key on the evidence used.
        var (orderProperties, charged) = await PropertyEvidenceAsync(db, charges, cases, docs, ct);
        var summary = ChargeLitigationLinker.Build(charges, cases, docs, await CompanyNamesAsync(db, requestId, ct), orderProperties, charged)
            with { SnapshotId = snapshotId, Version = version };

        // The app cache is registered with a SizeLimit, so every entry must declare a size (see #331).
        cache.Set(key, summary, new MemoryCacheEntryOptions().SetAbsoluteExpiration(CacheFor).SetSize(1));
        return summary;
    }
}
