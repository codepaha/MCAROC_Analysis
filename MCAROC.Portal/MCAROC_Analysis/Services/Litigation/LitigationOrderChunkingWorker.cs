namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues litigation order-document ids and chunks/embeds each in its own DI scope. Bounded
/// concurrency — each unit makes a real embedding API call (Vertex AI), the same reasoning
/// <c>LitigationOrderDocumentWorker</c> applies to PDF downloads (external calls, never unbounded like the
/// pure-CPU/DB <c>LitigationCasePersistenceWorker</c>).</summary>
public sealed class LitigationOrderChunkingWorker(
    IServiceScopeFactory scopes, LitigationOrderChunkingQueue queue, ILogger<LitigationOrderChunkingWorker> logger) : BackgroundService
{
    private const int MaxConcurrentChunking = 4;
    private readonly SemaphoreSlim _concurrency = new(MaxConcurrentChunking, MaxConcurrentChunking);

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
            var count = await scope.ServiceProvider.GetRequiredService<LitigationOrderChunkingOrchestrator>().RecoverStaleWorkAsync(ct);
            if (count > 0)
                logger.LogInformation("Re-queued {Count} litigation order document(s) for chunking on startup", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Litigation order chunking startup recovery failed");
        }
    }

    private async Task RunAsync(long orderDocumentId, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<LitigationOrderChunkingOrchestrator>().ChunkOrderDocumentAsync(orderDocumentId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down — recovery re-queues it */ }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled litigation order chunking failure for {OrderDocumentId}", orderDocumentId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
