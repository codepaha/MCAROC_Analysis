namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues litigation order-document ids and downloads/extracts each in its own DI scope. Bounded
/// concurrency, not unbounded like <c>LitigationCasePersistenceWorker</c> — this does real external HTTP
/// calls to the vendor's document host, and the vendor contract documents no rate/concurrency limit (see
/// docs/litigation-data-lake-integration.md), so this errs conservative. Mirrors
/// <c>LitigationSearchWorker</c>'s exact bounded-semaphore shape for the same reason (both talk to BPR).</summary>
public sealed class LitigationOrderDocumentWorker(
    IServiceScopeFactory scopes, LitigationOrderDocumentQueue queue, ILogger<LitigationOrderDocumentWorker> logger) : BackgroundService
{
    private const int MaxConcurrentDownloads = 4;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var orderDocumentId in queue.ReadAllAsync(stoppingToken))
        {
            await _concurrency.WaitAsync(stoppingToken);
            _ = RunAsync(orderDocumentId, stoppingToken);
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<LitigationOrderDocumentService>().RecoverStaleWorkAsync(ct);
            if (count > 0)
                logger.LogInformation("Re-queued {Count} litigation order document(s) for download on startup", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Litigation order document startup recovery failed");
        }
    }

    private async Task RunAsync(long orderDocumentId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LitigationOrderDocumentService>().DownloadAndExtractAsync(orderDocumentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled litigation order document failure for {OrderDocumentId}", orderDocumentId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
