using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Dequeues litigation order-document ids and chunks/embeds each in its own DI scope. Bounded
/// concurrency — each unit makes a real embedding API call (Vertex AI), the same reasoning
/// <c>LitigationOrderDocumentWorker</c> applies to PDF downloads (external calls, never unbounded like the
/// pure-CPU/DB <c>LitigationCasePersistenceWorker</c>). Continuously sweeps and drains outdated documents
/// in bounded batches until all documents are migrated to the active chunking version.</summary>
public sealed class LitigationOrderChunkingWorker(
    IServiceScopeFactory scopes,
    LitigationOrderChunkingQueue queue,
    ILogger<LitigationOrderChunkingWorker> logger,
    IOptions<LitigationChunkingOptions>? options = null) : BackgroundService
{
    private readonly LitigationChunkingOptions _options = options?.Value ?? new();
    private readonly SemaphoreSlim _concurrency = new(
        Math.Max(1, options?.Value.MaxConcurrentChunking ?? 4),
        Math.Max(1, options?.Value.MaxConcurrentChunking ?? 4));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);

        var consumerTask = ProcessQueueAsync(stoppingToken);
        var drainTask = DrainOutdatedContinuationAsync(stoppingToken);

        await Task.WhenAll(consumerTask, drainTask);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        await foreach (var orderDocumentId in queue.ReadAllAsync(ct))
        {
            await _concurrency.WaitAsync(ct);
            _ = RunAsync(orderDocumentId, ct);
        }
    }

    private async Task DrainOutdatedContinuationAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (queue.Count <= _options.QueueBackpressureLimit)
                {
                    using var scope = scopes.CreateScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<LitigationOrderChunkingOrchestrator>();
                    var enqueued = await orchestrator.EnqueueOutdatedDocumentsAsync(_options.OutdatedBatchSize, ct, _options.RequestId);
                    if (enqueued > 0)
                    {
                        logger.LogInformation("Enqueued {Count} outdated litigation order document(s) for re-embedding.", enqueued);
                        if (_options.BatchThrottleInterval > TimeSpan.Zero)
                            await Task.Delay(_options.BatchThrottleInterval, ct);
                        continue;
                    }

                    await Task.Delay(_options.SweepInterval, ct);
                    continue;
                }

                var delay = _options.BatchThrottleInterval > TimeSpan.Zero
                    ? _options.BatchThrottleInterval
                    : TimeSpan.FromMilliseconds(50);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during background re-embedding continuation sweep");
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task RecoverAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<LitigationOrderChunkingOrchestrator>();
            var count = await orchestrator.RecoverStaleWorkAsync(ct);
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
