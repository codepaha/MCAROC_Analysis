using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Builds a <see cref="PipelineSnapshot"/> with no-tracking reads only — it never writes, so the
/// coordinator can't corrupt any table it observes.</summary>
public class PipelineSnapshotReader(AppDbContext db, IConfiguration config, IOptions<BprLitigationOptions> bprOptions,
    IOptions<ReferenceToolOptions> referenceToolOptions, IOptions<ResolverOptions>? resolverOptions = null, LitigationAiAnalysisOrchestrator? litigationAnalysis = null)
{
    /// <summary>Failed order-document downloads after which the automatic litigation-analysis gate stops waiting for that document.</summary>
    public const int OrderDownloadGiveUpAttempts = 5;

    /// <summary>Cheap first (timestamps), and only then the evidence check: it runs only in the window after a court update finished and before a re-analysis caught up.</summary>
    private async Task<bool> ReanalysisDueAsync(long requestId, MCAROC_Analysis.Services.LitigationModule.Fetching.RefreshRunProgress? courtUpdate, CancellationToken ct)
    {
        if (litigationAnalysis is null) return false;
        var latest = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.LitigationAiAnalysisRunId).Select(r => new { r.Status, r.CompletedUtc, r.StartedUtc }).FirstOrDefaultAsync(ct);
        if (latest is null || latest.Status is not (LitigationAiAnalysisRunStatus.Completed or LitigationAiAnalysisRunStatus.CompletedWithErrors)) return false;
        if (latest.CompletedUtc is not { } done) return false;
        // The analysis loads its evidence near its START, so an order that arrived while it was running is not in it even though it arrived before it
        // finished: evidence is measured against when the run began, not when it completed.
        var readBoundary = latest.StartedUtc ?? done;
        // Two durable reasons to look again: a court update that finished after the analysis, or an order that became readable after it (an
        // earlier download failure the gate had stopped waiting for). Either way the evidence comparison decides whether a run is really needed.
        var courtNewer = courtUpdate?.FinishedUtc is { } courtFinished && done < courtFinished;
        if (!courtNewer)
        {
            if (!await OrderEvidenceAddedSinceAsync(db, requestId, readBoundary, ct)) return false;
            var now = DateTime.UtcNow;
            if (LastEvidenceComparisonUtc.TryGetValue(requestId, out var last) && now - last < EvidenceComparisonEvery) return false;
            LastEvidenceComparisonUtc[requestId] = now;
        }
        return !await litigationAnalysis.IsUpToDateAsync(requestId, ct);
    }

    /// <summary>Order text that became readable after <paramref name="sinceUtc"/>: chunks of the request created since then. The analysis reads chunks, so
    /// what matters is when the text became readable (chunked), not when the file was downloaded, which can be long before. A re-chunk of unchanged text also
    /// counts; the evidence comparison that follows decides whether a run is really needed.</summary>
    public static Task<bool> OrderEvidenceAddedSinceAsync(AppDbContext db, long requestId, DateTime sinceUtc, CancellationToken ct) =>
        db.LitigationOrderChunks.AsNoTracking().AnyAsync(c => c.RequestId == requestId && c.CreatedDate > sinceUtc, ct);

    // The evidence comparison loads every chunk of the request, so when nothing needs analysing it must not be repeated on every reconcile tick.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> LastEvidenceComparisonUtc = new();
    internal static readonly TimeSpan EvidenceComparisonEvery = TimeSpan.FromMinutes(10);

    public virtual async Task<PipelineSnapshot?> ReadAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => new
            {
                r.Cin, r.Llpin, r.RequestStatus, r.IsManualReviewRequired, r.ManualReviewReason, r.FailureReason,
                r.LatestCompletedIngestionRunId, r.HasIngestionWarnings, r.CompanyName
            })
            .FirstOrDefaultAsync(ct);
        if (request is null) return null;
        var litigationNameKnown = !string.IsNullOrWhiteSpace(request.CompanyName)
            && !string.Equals(request.CompanyName.Trim(), (request.Cin ?? request.Llpin)?.Trim(), StringComparison.OrdinalIgnoreCase);
        var unsearchedFormerNames = await LitigationStartService.UnsearchedFormerNamesAsync(db,
            new McaRequest { RequestId = requestId, LatestCompletedIngestionRunId = request.LatestCompletedIngestionRunId }, ct);

        var job = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .Select(j => new AutoFetchFacts(j.AutoFetchJobId, j.Status, j.RocDocumentId, j.IncludeFilings, j.FilingBatchId, j.FailureReason, j.HeartbeatUtc))
            .FirstOrDefaultAsync(ct);

        LifecycleFacts? lifecycle = null;
        if (job is not null)
        {
            var cin = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId).Select(j => j.Cin).FirstAsync(ct);
            lifecycle = await db.CompanyReportLifecycles.AsNoTracking().Where(l => l.Identifier == cin)
                .Select(l => new LifecycleFacts(l.State, l.UnlockedUtc, l.ActiveRefreshId != null)).FirstOrDefaultAsync(ct);
        }

        var latestResolution = await IdentityFacts.LatestAsync(db, requestId, appliedOnly: false, ct);
        var appliedResolution = latestResolution is { AppliedToRequest: true }
            ? latestResolution
            : await IdentityFacts.LatestAsync(db, requestId, appliedOnly: true, ct);

        var ingestionRunning = await db.IngestionRuns.AsNoTracking()
            .AnyAsync(i => i.RequestId == requestId && i.Status == IngestionRunStatus.Running, ct);
        var latestIngestionStatus = await db.IngestionRuns.AsNoTracking().Where(i => i.RequestId == requestId)
            .OrderByDescending(i => i.IngestionRunId).Select(i => (IngestionRunStatus?)i.Status).FirstOrDefaultAsync(ct);

        RunFacts<AnalysisRunStatus>? analysis = null;
        long? calcSnapshotId = null;
        CalculationAiAuditRunStatus? aiAuditStatus = null;
        var holdActive = false;
        var calcMode = CalculationAssuranceConfig.ParseMode(config);
        if (request.LatestCompletedIngestionRunId is { } ingestionRunId)
        {
            analysis = await db.AnalysisRuns.AsNoTracking()
                .Where(a => a.RequestId == requestId && a.IngestionRunId == ingestionRunId)
                .OrderByDescending(a => a.AnalysisRunId)
                .Select(a => new RunFacts<AnalysisRunStatus>(a.AnalysisRunId, a.Status, a.FailureReason, a.StartedDate))
                .FirstOrDefaultAsync(ct);

            if (analysis is not null)
            {
                calcSnapshotId = await db.CalculationAuditSnapshots.AsNoTracking()
                    .Where(s => s.RequestId == requestId && s.IngestionRunId == ingestionRunId && s.AnalysisRunId == analysis.Id)
                    .Select(s => (long?)s.CalculationAuditSnapshotId).FirstOrDefaultAsync(ct);
                if (calcSnapshotId is { } snapshotId)
                {
                    aiAuditStatus = await db.CalculationAiAuditRuns.AsNoTracking()
                        .Where(r => r.CalculationAuditSnapshotId == snapshotId)
                        .OrderByDescending(r => r.CalculationAiAuditRunId)
                        .Select(r => (CalculationAiAuditRunStatus?)r.Status).FirstOrDefaultAsync(ct);
                    var variant = DossierVariant.Executive.ToString();
                    holdActive = await db.CalculationArtifactHolds.AsNoTracking().AnyAsync(h =>
                        h.IsActive && h.CalculationAuditSnapshotId == snapshotId && (h.Variant == null || h.Variant == variant), ct);
                }
            }
        }

        var batchId = job?.FilingBatchId ?? await db.McaFilingBatches.AsNoTracking().Where(b => b.RequestId == requestId)
            .OrderByDescending(b => b.BatchId).Select(b => (long?)b.BatchId).FirstOrDefaultAsync(ct);
        FilingFacts? filings = null;
        if (batchId is { } bid)
        {
            var batch = await db.McaFilingBatches.AsNoTracking().Where(b => b.BatchId == bid)
                .Select(b => new { b.Status, b.FailureReason }).FirstOrDefaultAsync(ct);
            if (batch is not null)
            {
                var eligible = db.McaFilingDocuments.AsNoTracking().Where(d => d.BatchId == bid && d.DuplicateOfDocumentId == null
                    && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed);
                var outstanding = await eligible.CountAsync(d => d.ChunkingStatus == ChunkingStatus.Pending || d.ChunkingStatus == ChunkingStatus.InProgress, ct);
                var failed = await eligible.CountAsync(d => d.ChunkingStatus == ChunkingStatus.Failed, ct);
                filings = new FilingFacts(bid, batch.Status, batch.FailureReason, outstanding, failed);
            }
        }

        var search = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .Select(j => new { j.LitigationSearchJobId, j.Status, j.FailureReason, j.LeaseExpiresUtc }).FirstOrDefaultAsync(ct);
        LitigationSearchFacts? searchFacts = null;
        if (search is not null)
        {
            var snapshot = await db.LitigationReportSnapshots.AsNoTracking()
                .Where(s => s.LitigationSearchJobId == search.LitigationSearchJobId)
                .OrderByDescending(s => s.LitigationReportSnapshotId)
                .Select(s => new { s.LitigationReportSnapshotId, s.Status, s.LeaseExpiresUtc }).FirstOrDefaultAsync(ct);

            var orderDocs = await db.LitigationOrderDocuments.AsNoTracking()
                .Where(d => d.Order!.Case!.RequestId == requestId)
                .Select(d => new { d.Status, d.ChunkingStatus, d.CreatedUtc, d.AttemptCount }).ToListAsync(ct);
            // A download that keeps failing (an expired link or an error page served in place of the PDF) is retried for days; the automatic
            // stage must not wait that long for an order the source cannot serve. After OrderDownloadGiveUpAttempts it counts as settled
            // (the case is analysed without that order's text, and a later successful download still refreshes the findings).
            var ordersFullyProcessed = orderDocs.All(d =>
                d.Status is LitigationOrderDocumentStatus.Expired or LitigationOrderDocumentStatus.NotPublished
                || (d.Status == LitigationOrderDocumentStatus.Failed && d.AttemptCount >= OrderDownloadGiveUpAttempts)
                || (d.Status == LitigationOrderDocumentStatus.Downloaded && d.ChunkingStatus is ChunkingStatus.Chunked or ChunkingStatus.Failed));
            // #554: the properties each order names are read before the litigation stage moves on, so the report and dossier carry them.
            var readingOptions = new MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyReadingOptions
            {
                Enabled = !bool.TryParse(config[$"{MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyReadingOptions.SectionName}:Enabled"], out var enabled) || enabled
            };
            if (ordersFullyProcessed && MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyReadingService.IsActiveFor(readingOptions, config))
                ordersFullyProcessed = !await OrderPropertiesOutstandingAsync(requestId, ct);
            var staleCutoff = DateTime.UtcNow.AddDays(-5);
            var hasStalledOrderDownload = orderDocs.Any(d => d.Status == LitigationOrderDocumentStatus.Pending && d.CreatedUtc <= staleCutoff);

            // #489: the imported cases are the company's only once attributed; the stage is not done while any is unsettled.
            var outstanding = await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId)
                .Where(MCAROC_Analysis.Services.LitigationData.Attribution.LitigationAttribution.IsWaiting)
                .GroupBy(_ => 1).Select(g => new { Count = g.Count(), Since = g.Min(c => c.AttributionDecidedUtc) }).FirstOrDefaultAsync(ct);
            searchFacts = new LitigationSearchFacts(search.LitigationSearchJobId, search.Status, search.FailureReason,
                snapshot?.LitigationReportSnapshotId, snapshot?.Status, ordersFullyProcessed, hasStalledOrderDownload,
                search.LeaseExpiresUtc, snapshot?.LeaseExpiresUtc, outstanding?.Count ?? 0, outstanding?.Since);
        }

        var litigationAnalysis = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.LitigationAiAnalysisRunId)
            .Select(r => new RunFacts<LitigationAiAnalysisRunStatus>(r.LitigationAiAnalysisRunId, r.Status, r.FailureReason, null, r.LeaseExpiresUtc))
            .FirstOrDefaultAsync(ct);

        var courtUpdate = await MCAROC_Analysis.Services.LitigationModule.Fetching.RefreshRunService.LatestProgressAsync(db, requestId, ct);
        return new PipelineSnapshot
        {
            RequestId = requestId,
            Cin = request.Cin,
            Llpin = request.Llpin,
            RequestStatus = request.RequestStatus,
            IsManualReviewRequired = request.IsManualReviewRequired,
            ManualReviewReason = request.ManualReviewReason,
            RequestFailureReason = request.FailureReason,
            LatestResolution = latestResolution,
            AppliedResolution = appliedResolution,
            SpendThreshold = (resolverOptions?.Value ?? new ResolverOptions()).SpendThreshold,
            AutoFetch = job,
            RefreshGateEnabled = referenceToolOptions.Value.RefreshBeforeFetch,
            Lifecycle = lifecycle,
            LatestCompletedIngestionRunId = request.LatestCompletedIngestionRunId,
            HasIngestionWarnings = request.HasIngestionWarnings,
            IngestionRunning = ingestionRunning,
            LatestIngestionStatus = latestIngestionStatus,
            Analysis = analysis,
            CalcAssuranceMode = calcMode,
            CalcAuditSnapshotId = calcSnapshotId,
            CalcAiAuditStatus = aiAuditStatus,
            DossierHoldActive = holdActive,
            Filings = filings,
            LitigationConfigured = bprOptions.Value.IsConfigured,
            LitigationNameKnown = litigationNameKnown,
            UnsearchedFormerNames = unsearchedFormerNames,
            LitigationSearch = searchFacts,
            LitigationAnalysis = litigationAnalysis,
            CourtUpdate = courtUpdate,
            LitigationReanalysisDue = await ReanalysisDueAsync(requestId, courtUpdate, ct),
            CourtAgentsEnabled = config.GetValue<bool>("LitigationPortals:Agents:Enabled")
        };
    }

    /// <summary>A downloaded order with text, on a case the request shows, whose property reading has not finished (or not started).</summary>
    private Task<bool> OrderPropertiesOutstandingAsync(long requestId, CancellationToken ct) =>
        db.LitigationOrderDocuments.AsNoTracking().AnyAsync(d => d.Order!.Case!.RequestId == requestId
            && d.Status == LitigationOrderDocumentStatus.Downloaded && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
            && d.ExtractedText != null && d.Order.CollapsedIntoOrderId == null && !d.Order.IsSuperseded && d.Order.Case.MergedIntoCaseId == null
            && (d.Order.Case.AttributionOutcome == null || d.Order.Case.AttributionOutcome == LitigationAttributionOutcome.Attributed)
            && !db.LitigationOrderPropertyReadings.Any(r => r.LitigationOrderDocumentId == d.LitigationOrderDocumentId
                && (r.Status == LitigationOrderPropertyReadingStatus.Completed || r.Status == LitigationOrderPropertyReadingStatus.Failed
                    || r.Status == LitigationOrderPropertyReadingStatus.NoPropertyPages)), ct);
}
