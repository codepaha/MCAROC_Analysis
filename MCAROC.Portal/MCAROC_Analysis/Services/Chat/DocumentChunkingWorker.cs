namespace MCAROC_Analysis.Services.Chat;

/// <summary>Dequeues BatchIds and fans out per-document chunking with bounded concurrency (CPU/IO-bound
/// work, similar rationale to Phase 2's per-document concurrency limit).</summary>
public class DocumentChunkingWorker(
    IServiceScopeFactory scopeFactory,
    DocumentChunkingQueue queue,
    ILogger<DocumentChunkingWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(4);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        await foreach (var batchId in queue.ReadAllAsync(stoppingToken))
        {
            _ = HandleBatchAsync(batchId, stoppingToken);
        }
    }

    private async Task RunStartupRecoveryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<DocumentChunkingOrchestrator>();
            var recovered = await orchestrator.RecoverStaleWorkAsync(ct);
            if (recovered > 0)
                logger.LogInformation("Enqueued {Count} batch(es) for document chunking on startup (stale recovery + pending backlog)", recovered);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Document chunking startup recovery sweep failed");
        }
    }

    private async Task HandleBatchAsync(long batchId, CancellationToken ct)
    {
        List<long> documentIds;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<DocumentChunkingOrchestrator>();
            documentIds = await orchestrator.GetPendingDocumentIdsAsync(batchId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to list pending chunking documents for batch {BatchId}", batchId);
            return;
        }

        var tasks = documentIds.Select(docId => ChunkOneAsync(docId, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ChunkOneAsync(long filingDocumentId, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<DocumentChunkingOrchestrator>();
            await orchestrator.ChunkDocumentAsync(filingDocumentId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error chunking document {DocumentId}", filingDocumentId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
