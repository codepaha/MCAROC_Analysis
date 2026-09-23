using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Builds a <see cref="PipelineSnapshot"/> with no-tracking reads only — it never writes, so the
/// coordinator can't corrupt any table it observes.</summary>
public sealed class PipelineSnapshotReader(AppDbContext db, IConfiguration config, IOptions<BprLitigationOptions> bprOptions)
{
    public async Task<PipelineSnapshot?> ReadAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => new
            {
                r.Cin, r.Llpin, r.RequestStatus, r.IsManualReviewRequired, r.ManualReviewReason, r.FailureReason,
                r.LatestCompletedIngestionRunId, r.HasIngestionWarnings
            })
            .FirstOrDefaultAsync(ct);
        if (request is null) return null;

        var job = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .Select(j => new AutoFetchFacts(j.AutoFetchJobId, j.Status, j.RocDocumentId, j.IncludeFilings, j.FilingBatchId, j.FailureReason))
            .FirstOrDefaultAsync(ct);

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
                .Select(a => new RunFacts<AnalysisRunStatus>(a.AnalysisRunId, a.Status, a.FailureReason))
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
            .Select(j => new { j.LitigationSearchJobId, j.Status, j.FailureReason }).FirstOrDefaultAsync(ct);
        LitigationSearchFacts? searchFacts = null;
        if (search is not null)
        {
            var snapshot = await db.LitigationReportSnapshots.AsNoTracking()
                .Where(s => s.LitigationSearchJobId == search.LitigationSearchJobId)
                .OrderByDescending(s => s.LitigationReportSnapshotId)
                .Select(s => new { s.LitigationReportSnapshotId, s.Status }).FirstOrDefaultAsync(ct);
            searchFacts = new LitigationSearchFacts(search.LitigationSearchJobId, search.Status, search.FailureReason,
                snapshot?.LitigationReportSnapshotId, snapshot?.Status);
        }

        var litigationAnalysis = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId)
            .OrderByDescending(r => r.LitigationAiAnalysisRunId)
            .Select(r => new RunFacts<LitigationAiAnalysisRunStatus>(r.LitigationAiAnalysisRunId, r.Status, r.FailureReason))
            .FirstOrDefaultAsync(ct);

        return new PipelineSnapshot
        {
            RequestId = requestId,
            Cin = request.Cin,
            Llpin = request.Llpin,
            RequestStatus = request.RequestStatus,
            IsManualReviewRequired = request.IsManualReviewRequired,
            ManualReviewReason = request.ManualReviewReason,
            RequestFailureReason = request.FailureReason,
            AutoFetch = job,
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
            LitigationSearch = searchFacts,
            LitigationAnalysis = litigationAnalysis
        };
    }
}
