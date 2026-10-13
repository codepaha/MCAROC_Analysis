using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Every <see cref="ReferenceToolOptions.RefreshPollMinutes"/>: for each company with parked jobs
/// (<see cref="AutoFetchJobStatus.WaitingForRefresh"/> or <see cref="AutoFetchJobStatus.WaitingForUnlock"/>),
/// lets <see cref="CompanyGateCoordinator"/> poll the refresh, spend an approved unlock, and re-queue whatever can
/// move on. The waiting state lives in the database, so a restart loses nothing: startup recovery re-queues
/// parked jobs and they simply re-park or proceed.</summary>
public sealed class CompanyRefreshWorker(IServiceScopeFactory scopes, IOptions<ReferenceToolOptions> options, ILogger<CompanyRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.RefreshPollMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Company refresh/unlock poll tick failed");
            }
        }
    }

    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        List<(string Cin, string Bid)> companies;
        using (var scope = scopes.CreateScope())
        {
            var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();
            if (await health.IsOpenAsync(IntegrationName.ReferenceTool, ct)) return 0;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            companies = (await db.AutoFetchJobs.AsNoTracking()
                    .Where(j => j.Status == AutoFetchJobStatus.WaitingForRefresh || j.Status == AutoFetchJobStatus.WaitingForUnlock)
                    .Select(j => new { j.Cin, j.Bid }).Distinct().ToListAsync(ct))
                .Select(c => (c.Cin, c.Bid)).ToList();
            // A refresh that requests exported past (fetch now, update later) is followed to its end even with no job waiting on it.
            var refreshing = await db.CompanyReportLifecycles.AsNoTracking().Where(l => l.ActiveRefreshId != null).Select(l => l.Identifier).ToListAsync(ct);
            foreach (var identifier in refreshing.Where(i => companies.All(c => !string.Equals(c.Cin, i, StringComparison.OrdinalIgnoreCase))))
            {
                var bid = await db.AutoFetchJobs.AsNoTracking().Where(j => j.Cin == identifier && j.Bid != "").OrderByDescending(j => j.AutoFetchJobId)
                    .Select(j => j.Bid).FirstOrDefaultAsync(ct);
                if (bid is not null) companies.Add((identifier, bid));
            }
        }

        var requeued = 0;
        try { requeued += await QueueUpdatesAfterRefreshAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Queueing updates after landed refreshes failed; will retry next tick"); }
        foreach (var (cin, bid) in companies)
        {
            try
            {
                using var scope = scopes.CreateScope();
                requeued += await scope.ServiceProvider.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(cin, bid, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Polling the reference-tool gate for {Cin} failed; will retry next tick", cin);
            }
        }
        try { requeued += await QueueUpdatesAfterRefreshAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested) { logger.LogWarning(ex, "Queueing updates after landed refreshes failed; will retry next tick"); }
        return requeued;
    }

    /// <summary>How far back a landed refresh is looked at, and how long before it an export still counts as "made while it was pending".</summary>
    internal static readonly TimeSpan RecentRefresh = TimeSpan.FromDays(2);
    internal static readonly TimeSpan ExportWindow = TimeSpan.FromDays(7);

    /// <summary>Fetch now, update later: once a company's refresh has landed, every request that exported the company before it (within
    /// <see cref="ExportWindow"/>) is re-checked, so it picks up the new data and the report is rebuilt. A request that is busy right now is simply
    /// found again on the next tick: the condition (its export predates the landed refresh) stays true until its re-check completes.</summary>
    public async Task<int> QueueUpdatesAfterRefreshAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var jobs = scope.ServiceProvider.GetService<AutoFetchJobService>();
        var queue = scope.ServiceProvider.GetService<AutoFetchQueue>();
        if (jobs is null || queue is null) return 0;
        var since = DateTime.UtcNow - RecentRefresh;
        var stale = await (from l in db.CompanyReportLifecycles.AsNoTracking()
                           where l.ActiveRefreshId == null && l.LastRefreshCompletedUtc != null && l.LastRefreshCompletedUtc >= since
                           join j in db.AutoFetchJobs.AsNoTracking() on l.Identifier equals j.Cin
                           where (j.Status == AutoFetchJobStatus.Completed || j.Status == AutoFetchJobStatus.CompletedWithWarnings)
                               && j.CompletedUtc != null && j.CompletedUtc < l.LastRefreshCompletedUtc
                               && j.CompletedUtc >= l.LastRefreshCompletedUtc!.Value.AddDays(-7)
                           select j.RequestId).Distinct().ToListAsync(ct);
        var queued = 0;
        foreach (var requestId in stale)
        {
            var job = await jobs.TryQueueRecheckAsync(requestId, $"refresh-landed:{requestId}", ct);
            if (job is null) continue;   // busy: found again next tick
            queue.Enqueue(job.AutoFetchJobId);
            queued++;
            logger.LogInformation("The reference tool's refresh landed for request {RequestId}'s company; re-checking it so the report updates", requestId);
        }
        return queued;
    }
}
