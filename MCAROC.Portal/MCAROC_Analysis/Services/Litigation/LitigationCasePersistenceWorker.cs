namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues completed litigation search jobs and persists their cases in its own DI scope. Pure
/// CPU/DB work (JSON parsing + EF writes, no external HTTP calls), so — unlike the vendor-facing workers in
/// this lane — there is no reason to cap concurrency below the default of "as fast as items arrive."
///
/// Deliberately unbounded, not just "no reason to cap" — this worker relies on
/// <see cref="LitigationCasePersistenceService"/>'s own atomic, RowVersion-protected claim to make
/// concurrent execution *safe*, not on serializing dispatch to make it unnecessary. Two duplicate queue
/// messages for the same job (e.g. one from a real completion, one from a startup recovery sweep racing it)
/// legitimately run at the same time; exactly one of them wins the claim on the underlying
/// <c>LitigationReportSnapshot</c> and the other's <c>SaveChangesAsync</c> throws
/// <see cref="DbUpdateConcurrencyException"/> and stops immediately.</summary>
public sealed class LitigationCasePersistenceWorker(
    IServiceScopeFactory scopes, LitigationCasePersistenceQueue queue, ILogger<LitigationCasePersistenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken))
            _ = RunAsync(jobId, stoppingToken);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var jobIds = await scope.ServiceProvider.GetRequiredService<LitigationCasePersistenceService>()
                .FindUnprocessedCompletedJobIdsAsync(ct);
            foreach (var jobId in jobIds) queue.Enqueue(jobId);
            if (jobIds.Count > 0)
                logger.LogInformation("Re-queued {Count} completed litigation search job(s) for case persistence on startup", jobIds.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Litigation case persistence startup recovery failed");
        }
    }

    private async Task RunAsync(long jobId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LitigationCasePersistenceService>().PersistCasesForJobAsync(jobId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled litigation case persistence failure for job {JobId}", jobId);
        }
    }
}
