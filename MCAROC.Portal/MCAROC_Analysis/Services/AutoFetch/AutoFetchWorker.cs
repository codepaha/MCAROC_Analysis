using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Dequeues auto-fetch jobs and runs each in its own DI scope. Two at a time: every job is a
/// long conversation with one third-party session (hundreds to thousands of PDF downloads), and the
/// per-job download concurrency already parallelises the expensive part.</summary>
public sealed class AutoFetchWorker(IServiceScopeFactory scopes, AutoFetchQueue queue, IOptions<ReferenceToolOptions> options, ILogger<AutoFetchWorker> logger) : BackgroundService
{
    private const int MaxConcurrentJobs = 2;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentJobs, MaxConcurrentJobs);
    private readonly ReferenceToolOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        _ = RunRequeueSweepAsync(stoppingToken);

        while (await queue.WaitToReadAsync(stoppingToken))
        {
            // Gate before dequeuing (docs/pipeline-automation-plan.md §5.4 mechanism 1): an item is never
            // taken off the channel while the breaker is open, so there is nothing to lose if the app
            // restarts while waiting here.
            await WaitUntilReferenceToolAvailableAsync(stoppingToken);
            if (!queue.TryRead(out var jobId)) continue;
            await _concurrency.WaitAsync(stoppingToken);
            _ = RunAsync(jobId, stoppingToken);
        }
    }

    private async Task WaitUntilReferenceToolAvailableAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var scope = scopes.CreateScope();
            var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();
            if (!await health.IsOpenAsync(IntegrationName.ReferenceTool, ct)) return;
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
    }

    /// <summary>§5.4 mechanism 4: repairs *any* lost enqueue (a claim rejected by the breaker predicate, an
    /// app restart in the gap before <see cref="RecoverAsync"/> ran, anything else) by periodically
    /// re-enqueuing every <c>Queued</c> job whose last heartbeat (or creation, if it was never claimed) is
    /// older than the sweep interval. Safe to over-fire: a job already in the channel or already claimed
    /// by another worker just fails the atomic claim in <see cref="AutoFetchJobService.ProcessAsync"/> and
    /// is silently dropped, so a duplicate enqueue here is harmless.</summary>
    private async Task RunRequeueSweepAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _opts.RequeueSweepMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();
                if (await health.IsOpenAsync(IntegrationName.ReferenceTool, ct)) continue;

                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var staleBefore = DateTime.UtcNow - interval;
                var stale = await db.AutoFetchJobs
                    .Where(j => j.Status == AutoFetchJobStatus.Queued && (j.HeartbeatUtc ?? j.CreatedUtc) < staleBefore)
                    .Select(j => j.AutoFetchJobId)
                    .ToListAsync(ct);
                foreach (var id in stale) queue.Enqueue(id);
                if (stale.Count > 0)
                    logger.LogInformation("Re-enqueue sweep found {Count} stale Queued auto-fetch job(s)", stale.Count);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Auto-fetch re-enqueue sweep tick failed");
            }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    /// <summary>A job interrupted by a restart is put back to Queued and re-enqueued. The job service
    /// resumes from its checkpoints (documents/ingestion run/filings already produced are kept, staged
    /// PDFs already on disk are not re-downloaded), so this is safe to repeat.</summary>
    internal async Task RecoverAsync(CancellationToken ct, long? requestId = null)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var interrupted = await db.AutoFetchJobs
                .Where(j => (requestId == null || j.RequestId == requestId)
                    && j.Status != AutoFetchJobStatus.Completed
                    && j.Status != AutoFetchJobStatus.CompletedWithWarnings
                    && j.Status != AutoFetchJobStatus.Failed)
                .ToListAsync(ct);
            foreach (var job in interrupted)
            {
                job.Status = AutoFetchJobStatus.Queued;
                job.StatusMessage = "Resuming after an application restart.";
            }
            await db.SaveChangesAsync(ct);
            foreach (var job in interrupted) queue.Enqueue(job.AutoFetchJobId);
            if (interrupted.Count > 0)
                logger.LogInformation("Re-queued {Count} interrupted auto-fetch job(s) on startup", interrupted.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-fetch startup recovery failed");
        }
    }

    private async Task RunAsync(long jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AutoFetchJobService>().ProcessAsync(jobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled auto-fetch job failure for {JobId}", jobId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
