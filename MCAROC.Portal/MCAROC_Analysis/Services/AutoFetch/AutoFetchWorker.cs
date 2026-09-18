using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Dequeues auto-fetch jobs and runs each in its own DI scope. Two at a time: every job is a
/// long conversation with one third-party session (hundreds to thousands of PDF downloads), and the
/// per-job download concurrency already parallelises the expensive part.</summary>
public sealed class AutoFetchWorker(IServiceScopeFactory scopes, AutoFetchQueue queue, ILogger<AutoFetchWorker> logger) : BackgroundService
{
    private const int MaxConcurrentJobs = 2;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentJobs, MaxConcurrentJobs);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
        {
            await _concurrency.WaitAsync(stoppingToken);
            _ = RunAsync(jobId, stoppingToken);
        }
    }

    /// <summary>A job interrupted by a restart is put back to Queued and re-enqueued. The job service
    /// resumes from its checkpoints (documents/ingestion run/filings already produced are kept, staged
    /// PDFs already on disk are not re-downloaded), so this is safe to repeat.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var interrupted = await db.AutoFetchJobs
                .Where(j => j.Status != AutoFetchJobStatus.Completed
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
