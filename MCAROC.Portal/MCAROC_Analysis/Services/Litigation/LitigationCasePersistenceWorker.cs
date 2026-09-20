namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues litigation report snapshot ids and persists their cases in its own DI scope. Pure CPU/DB
/// work (JSON parsing + EF writes, no external HTTP calls), so — unlike the vendor-facing workers in this
/// lane — there is no reason to cap concurrency below the default of "as fast as items arrive."
///
/// Deliberately unbounded, not just "no reason to cap" — this worker relies on
/// <see cref="LitigationCasePersistenceService"/>'s own atomic, RowVersion-protected claim to make
/// concurrent execution *safe*, not on serializing dispatch to make it unnecessary. Two duplicate queue
/// messages for the same snapshot (e.g. one from a real completion, one from a startup recovery sweep racing
/// it) legitimately run at the same time; exactly one of them wins the claim and the other's
/// <c>SaveChangesAsync</c> throws <see cref="DbUpdateConcurrencyException"/> and stops immediately. Recovery
/// itself (including delayed re-checks for a still-valid lease) lives on the service, not here — this worker
/// is just dispatch, mirroring <c>LitigationSearchWorker</c>'s own split.</summary>
public sealed class LitigationCasePersistenceWorker(
    IServiceScopeFactory scopes, LitigationCasePersistenceQueue queue, ILogger<LitigationCasePersistenceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var snapshotId in queue.ReadAllAsync(stoppingToken))
            _ = RunAsync(snapshotId, stoppingToken);
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<LitigationCasePersistenceService>().RecoverStaleWorkAsync(ct);
            if (count > 0)
                logger.LogInformation("Re-queued {Count} litigation report snapshot(s) for case persistence on startup", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Litigation case persistence startup recovery failed");
        }
    }

    private async Task RunAsync(long snapshotId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LitigationCasePersistenceService>().PersistSnapshotAsync(snapshotId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled litigation case persistence failure for snapshot {SnapshotId}", snapshotId);
        }
    }
}
