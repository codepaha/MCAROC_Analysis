using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Dequeues BatchIds and fans out per-document chunking with bounded concurrency (CPU/IO-bound
/// work, similar rationale to Phase 2's per-document concurrency limit) — each unit makes an embedding
/// API call, so this shares the same LargeArchiveUploadOptions:MaxConcurrentChunking config as the rest of
/// the pipeline's tunable limits rather than a hardcoded value.</summary>
public class DocumentChunkingWorker(
    IServiceScopeFactory scopeFactory,
    DocumentChunkingQueue queue,
    ILogger<DocumentChunkingWorker> logger,
    IOptions<LargeArchiveUploadOptions>? options = null) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(Math.Max(1, options?.Value.MaxConcurrentChunking ?? 4));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        await foreach (var batchId in queue.ReadAllAsync(stoppingToken))
        {
            // Different batches still run concurrently (fire-and-forget), but each batch transitions to
            // "running" right here — from this point, a concurrent Enqueue can no longer be a silent
            // no-op; it flags exactly one follow-up pass instead. See DocumentChunkingQueue's remarks.
            queue.MarkStarted(batchId);
            _ = RunPassAsync(batchId, stoppingToken);
        }
    }

    private async Task RunPassAsync(long batchId, CancellationToken ct)
    {
        try
        {
            await HandleBatchAsync(batchId, ct);
        }
        finally
        {
            queue.MarkDone(batchId);
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
