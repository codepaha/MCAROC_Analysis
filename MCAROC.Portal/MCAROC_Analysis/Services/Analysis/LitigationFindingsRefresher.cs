using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Keeps an analysis's findings in step with the request's litigation (owner, 2026-10-26). The litigation service's cases land
/// after the analysis has run, and change with every refresh, hide or attribution, each of which moves the request's live-data version.
/// When that version has moved past the one a request's latest analysis was evaluated at, the deterministic rules run again on the same
/// inputs an analysis reads (no AI call, no cost) and replace the run's rule findings, counts, review priority and data-sufficiency notes;
/// the AI's cross-section findings and executive summary stay as written, and a finding that did not change keeps its AI narrative. When
/// nothing changed, only the version is recorded.</summary>
public sealed class LitigationFindingsRefresher(
    AppDbContext db, ILogger<LitigationFindingsRefresher> logger, LitigationReaderGate? readerGate = null,
    DossierCache? dossierCache = null, IWebHostEnvironment? env = null)
{
    /// <summary>Requests whose latest completed analysis (of their latest ingestion) was evaluated at another litigation version than
    /// the request's own, and that no analysis is running for.</summary>
    public Task<List<long>> DueAsync(int max, CancellationToken ct) =>
        db.Requests.AsNoTracking()
            .Where(r => r.RequestStatus != RequestStatus.Cancelled && r.LatestCompletedIngestionRunId != null
                && !db.AnalysisRuns.Any(a => a.RequestId == r.RequestId && a.Status == AnalysisRunStatus.Running))
            .Select(r => new
            {
                r.RequestId, r.LiveDataVersion,
                Run = db.AnalysisRuns
                    .Where(a => a.RequestId == r.RequestId && a.IngestionRunId == r.LatestCompletedIngestionRunId
                        && (a.Status == AnalysisRunStatus.Completed || a.Status == AnalysisRunStatus.CompletedWithErrors))
                    .OrderByDescending(a => a.RunNumber).Select(a => new { a.LitigationFindingsVersion }).FirstOrDefault()
            })
            .Where(x => x.Run != null && (x.Run.LitigationFindingsVersion == null || x.Run.LitigationFindingsVersion != x.LiveDataVersion))
            .OrderBy(x => x.RequestId).Select(x => x.RequestId).Take(max).ToListAsync(ct);

    /// <summary>Re-evaluates one request. Returns true when its findings changed.</summary>
    public async Task<bool> RefreshAsync(long requestId, CancellationToken ct)
    {
        await using var fence = await SqlSessionLock.TryAcquireAsync(db, $"MCAROC:LitigationFindings:{requestId}", ct);
        if (fence is null) return false;

        var request = await db.Requests.AsNoTracking().Include(r => r.Client).FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request?.LatestCompletedIngestionRunId is not { } ingestionRunId) return false;
        var run = await db.AnalysisRuns.AsNoTracking()
            .Where(a => a.RequestId == requestId && a.IngestionRunId == ingestionRunId
                && (a.Status == AnalysisRunStatus.Completed || a.Status == AnalysisRunStatus.CompletedWithErrors))
            .OrderByDescending(a => a.RunNumber)
            .Select(a => new
            {
                a.AnalysisRunId, a.LitigationFindingsVersion, a.OverallReviewPriority, a.DataSufficiencyNotesJson,
                a.CriticalFindingsCount, a.ReviewFindingsCount, a.WatchFindingsCount, a.PositiveFindingsCount
            })
            .FirstOrDefaultAsync(ct);
        if (run is null) return false;

        var ctx = await AnalysisContextLoader.LoadAsync(db, readerGate, request, ingestionRunId, ct);
        var result = RuleEngine.Evaluate(ctx, RuleThresholds.Default);
        var version = ctx.Litigation.LiveDataVersion;

        var stored = await db.AnalysisFindings
            .Where(f => f.AnalysisRunId == run.AnalysisRunId && !f.Code.StartsWith(AnalysisOrchestrator.AiCrossSectionCodePrefix))
            .ToListAsync(ct);
        var findingsChanged = !stored.Select(Key).OrderBy(k => k, StringComparer.Ordinal)
            .SequenceEqual(result.Findings.Select(Key).OrderBy(k => k, StringComparer.Ordinal), StringComparer.Ordinal);
        // The run's own metadata is compared on its own: a search that completes with nothing to report changes no finding, but it does retire
        // the "search has not completed" note, and that must not stay marked current.
        var (critical, review, watch, positive) = (Count(FindingSeverity.Critical), Count(FindingSeverity.Review), Count(FindingSeverity.Watch), Count(FindingSeverity.Positive));
        var notes = AnalysisOrchestrator.SufficiencyJson(result);
        var metadataChanged = run.OverallReviewPriority != result.OverallReviewPriority || run.DataSufficiencyNotesJson != notes
            || run.CriticalFindingsCount != critical || run.ReviewFindingsCount != review || run.WatchFindingsCount != watch || run.PositiveFindingsCount != positive;
        var changed = findingsChanged || metadataChanged;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only the version this pass started from may be replaced: another pass that got there first wins.
        var claimed = await db.AnalysisRuns
            .Where(a => a.AnalysisRunId == run.AnalysisRunId && a.LitigationFindingsVersion == run.LitigationFindingsVersion)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.LitigationFindingsVersion, version), ct);
        if (claimed == 0) { await tx.RollbackAsync(ct); return false; }

        if (findingsChanged)
        {
            var narratives = stored.GroupBy(Key).ToDictionary(g => g.Key, g => (g.First().WhyThisMatters, g.First().RecommendedReview));
            db.AnalysisFindings.RemoveRange(stored);
            foreach (var draft in result.Findings)
            {
                var entity = AnalysisOrchestrator.ToEntity(draft, run.AnalysisRunId, requestId);
                if (narratives.TryGetValue(Key(draft), out var n)) (entity.WhyThisMatters, entity.RecommendedReview) = n;
                db.AnalysisFindings.Add(entity);
            }
            await db.SaveChangesAsync(ct);
        }
        if (metadataChanged)
            await db.AnalysisRuns.Where(a => a.AnalysisRunId == run.AnalysisRunId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.CriticalFindingsCount, critical)
                    .SetProperty(a => a.ReviewFindingsCount, review)
                    .SetProperty(a => a.WatchFindingsCount, watch)
                    .SetProperty(a => a.PositiveFindingsCount, positive)
                    .SetProperty(a => a.OverallReviewPriority, result.OverallReviewPriority)
                    .SetProperty(a => a.DataSufficiencyNotesJson, notes), ct);
        await tx.CommitAsync(ct);

        if (changed)
        {
            logger.LogInformation("Findings of analysis run {AnalysisRunId} (request {RequestId}) re-evaluated at litigation version {Version}: {Count} rule finding(s), priority {Priority}",
                run.AnalysisRunId, requestId, version, result.Findings.Count, result.OverallReviewPriority);
            // The dossier prints the review priority, the findings and the data-sufficiency notes; its caches are keyed on the analysis run,
            // which did not change.
            if (dossierCache is not null && env is not null)
            {
                try { await dossierCache.InvalidateForRequestAsync(requestId, env.ContentRootPath, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogWarning(ex, "Clearing the dossier cache failed for request {RequestId}", requestId); }
            }
        }
        return changed;

        int Count(FindingSeverity severity) => result.Findings.Count(f => f.Severity == severity);
    }

    private static string Key(FindingDraft f) => Key(f.Code, f.Severity, f.SummaryText, f.SourceReferenceJson, f.MetricsJson);
    private static string Key(AnalysisFinding f) => Key(f.Code, f.Severity, f.SummaryText, f.SourceReferenceJson, f.MetricsJson);
    private static string Key(string code, FindingSeverity severity, string? summary, string? source, string? metrics) =>
        string.Join('\u001f', code, severity, summary, source, metrics);
}

/// <summary>Polls for analyses whose litigation findings are behind the request's litigation version and re-evaluates them.</summary>
public sealed class LitigationFindingsRefreshWorker(IServiceScopeFactory scopes, ILogger<LitigationFindingsRefreshWorker> logger) : BackgroundService
{
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);
    private const int BatchSize = 10;
    // A request whose pass failed waits before it is tried again, so it never holds the head of every batch.
    private readonly Dictionary<long, DateTime> _failedUntil = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var refresher = scope.ServiceProvider.GetRequiredService<LitigationFindingsRefresher>();
                var now = DateTime.UtcNow;
                foreach (var stale in _failedUntil.Where(f => f.Value <= now).Select(f => f.Key).ToList()) _failedUntil.Remove(stale);
                var due = (await refresher.DueAsync(BatchSize + _failedUntil.Count, stoppingToken)).Where(id => !_failedUntil.ContainsKey(id)).Take(BatchSize);
                foreach (var requestId in due)
                {
                    try
                    {
                        using var one = scopes.CreateScope();
                        await one.ServiceProvider.GetRequiredService<LitigationFindingsRefresher>().RefreshAsync(requestId, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Re-evaluating the litigation findings failed for request {RequestId}", requestId);
                        _failedUntil[requestId] = DateTime.UtcNow + FailureBackoff;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "The litigation findings refresh pass failed");
            }
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
