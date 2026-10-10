using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginReportWorker(IServiceScopeFactory scopes, PreLoginReportQueue queue, ILogger<PreLoginReportWorker> logger) : BackgroundService
{
    // Each job makes a live call to a third-party API (InstaFinancials) that can take several minutes —
    // without a cap, clicking Rerun on many jobs at once (any mix of SBI/PRR; format doesn't matter to the
    // queue) would fire that many concurrent calls to the vendor. Jobs beyond this limit just wait their
    // turn in the queue (visible in History as "Queued") instead of all starting at once.
    private const int MaxConcurrentJobs = 3;
    private readonly SemaphoreSlim concurrency = new(MaxConcurrentJobs, MaxConcurrentJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            await concurrency.WaitAsync(stoppingToken);
            _ = RunAsync(jobId, stoppingToken);
        }
    }
    private async Task RecoverAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // A job left mid-run by a restart is reset to Queued before re-enqueue — otherwise a
        // Fetching/Generating job would be skipped by ProcessAsync's own in-flight guard.
        // AwaitingLitigation jobs are preserved without status reset.
        var interrupted = await db.PreLoginReportJobs
            .Where(x => x.Status == PreLoginReportJobStatus.Queued || x.Status == PreLoginReportJobStatus.Fetching || x.Status == PreLoginReportJobStatus.Generating || x.Status == PreLoginReportJobStatus.AwaitingLitigation)
            .ToListAsync(token);
        foreach (var job in interrupted)
        {
            if (job.Status != PreLoginReportJobStatus.AwaitingLitigation)
            {
                job.Status = PreLoginReportJobStatus.Queued;
                job.ProgressPercent = 0;
            }
        }
        await db.SaveChangesAsync(token);

        var targetService = scope.ServiceProvider.GetRequiredService<PreLoginLitigationTargetService>();
        await targetService.RecoverStaleWorkAsync(token);

        var targetQueue = scope.ServiceProvider.GetRequiredService<PreLoginLitigationTargetQueue>();
        var pendingTargets = await db.PreLoginLitigationTargets
            .Where(t => t.Status == "Pending" || t.Status == "Polling")
            .Select(t => t.PreLoginLitigationTargetId)
            .ToListAsync(token);
        foreach (var tId in pendingTargets) targetQueue.Enqueue(tId);

        foreach (var job in interrupted) queue.Enqueue(job.PreLoginReportJobId);
    }
    private async Task RunAsync(long jobId, CancellationToken token)
    {
        try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<PreLoginReportJobService>().ProcessAsync(jobId, token); }
        catch (Exception ex) { logger.LogError(ex, "Unhandled pre-login report job failure for {JobId}", jobId); }
        finally { concurrency.Release(); }
    }
}
