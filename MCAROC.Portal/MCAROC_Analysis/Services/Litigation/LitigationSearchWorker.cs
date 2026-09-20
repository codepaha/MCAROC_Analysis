namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues litigation search jobs and runs each in its own DI scope. Two at a time — mirrors
/// <c>AutoFetchWorker</c>'s reasoning: each job is a long conversation with one external vendor session
/// (authenticate, register, poll for up to 30 minutes), and there is no CPU-heavy work here to parallelize
/// further.</summary>
public sealed class LitigationSearchWorker(
    IServiceScopeFactory scopes, LitigationSearchQueue queue, ILogger<LitigationSearchWorker> logger) : BackgroundService
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

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<LitigationSearchJobService>().RecoverStaleWorkAsync(ct);
            if (count > 0)
                logger.LogInformation("Re-queued {Count} litigation search job(s) on startup", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Litigation search startup recovery failed");
        }
    }

    private async Task RunAsync(long jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LitigationSearchJobService>().ProcessAsync(jobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled litigation search job failure for {JobId}", jobId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
