using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Finishes what adding a name variation starts, for requests whose pipeline has long completed (the pipeline reconciler only looks at live
/// runs):
/// <list type="number">
/// <item>a variation added while the request's search was running is searched once that search is done (the add could not start a second one);</item>
/// <item>once a search that carried variations has landed, its cases are attributed and every order is downloaded and chunked, the litigation analysis
/// is started for what changed (each case is reused unless its evidence changed: <see cref="LitigationCaseAnalysisInputBuilder.SameEvidence"/>).</item>
/// </list>
/// Each search is started at most once per keyword set: the job stores its keywords when it is created, so a variation it carried is never "unsearched"
/// again, even if that search fails.</summary>
public sealed class NameVariationSearchWorker(IServiceScopeFactory scopes, ILogger<NameVariationSearchWorker> logger) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                await PassAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The name-variation search pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal static async Task PassAsync(IServiceProvider services, CancellationToken ct)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var logger = services.GetRequiredService<ILogger<NameVariationSearchWorker>>();
        // Only the requests a variation was added on: the others pick the variations up at their next search.
        var requestIds = await db.CompanyNameVariations.AsNoTracking().Where(v => v.RemovedUtc == null)
            .Select(v => v.AddedOnRequestId).Distinct().ToListAsync(ct);
        foreach (var requestId in requestIds)
        {
            var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId && r.RequestStatus != RequestStatus.Cancelled, ct);
            if (request is null) continue;
            var job = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
                .Select(j => new { j.LitigationSearchJobId, j.Status, j.KeywordsJson }).FirstOrDefaultAsync(ct);
            if (job is null) continue;   // no search yet: the request's first search plans the variations itself

            // Manual: the search was asked for by the person who added the names (the client refresh limits were checked then); it is bounded
            // by StartPendingSearchAsync, which never buys the same keyword set again.
            if (job.Status is LitigationSearchJobStatus.Completed or LitigationSearchJobStatus.Failed
                && await services.GetRequiredService<NameVariationService>().StartPendingSearchAsync(requestId, ct) is { } search)
            {
                logger.LogInformation("Search with the added name variations for request {RequestId}: {Outcome}", requestId,
                    search.Started ? "started" : search.Message);
                continue;
            }

            if (job.Status == LitigationSearchJobStatus.Completed && await ReadyForAnalysisAsync(db, requestId, job.LitigationSearchJobId, job.KeywordsJson, ct))
            {
                var result = await services.GetRequiredService<LitigationStartService>().StartAutoAnalysisAsync(requestId, null, ct);
                if (result.Started)
                    logger.LogInformation("Litigation analysis started for request {RequestId} after a search with added name variations", requestId);
            }
        }
    }

    /// <summary>The search carried an added variation, its report is in, nothing waits for attribution, every order is downloaded and chunked, a litigation
    /// analysis exists from before (a request never analysed is left to its own pipeline) and none has run on this report yet.</summary>
    private static async Task<bool> ReadyForAnalysisAsync(AppDbContext db, long requestId, long jobId, string? keywordsJson, CancellationToken ct)
    {
        if (!LitigationKeyword.ParseJson(keywordsJson).Any(k => k.Source == LitigationKeywordSource.AddedNameVariation)) return false;
        var snapshotId = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.LitigationSearchJobId == jobId && s.Status == LitigationReportSnapshotStatus.Completed)
            .OrderByDescending(s => s.LitigationReportSnapshotId).Select(s => (long?)s.LitigationReportSnapshotId).FirstOrDefaultAsync(ct);
        if (snapshotId is not { } sid) return false;
        var runs = db.LitigationAiAnalysisRuns.AsNoTracking().Where(r => r.RequestId == requestId);
        if (!await runs.AnyAsync(ct) || await runs.AnyAsync(r => r.TriggerSnapshotId == sid, ct)) return false;
        if (await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId).AnyAsync(LitigationAttribution.IsWaiting, ct)) return false;
        return !await db.LitigationOrderDocuments.AsNoTracking().Where(d => d.Order!.Case!.RequestId == requestId).AnyAsync(d =>
            !(d.Status == LitigationOrderDocumentStatus.Expired || d.Status == LitigationOrderDocumentStatus.NotPublished
              || (d.Status == LitigationOrderDocumentStatus.Downloaded && (d.ChunkingStatus == ChunkingStatus.Chunked || d.ChunkingStatus == ChunkingStatus.Failed))), ct);
    }
}
