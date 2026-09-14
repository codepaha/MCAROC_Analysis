using System.Threading;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>Dequeues MCA Filings work and dispatches it to a scoped FilingBatchProcessor. Two separate
/// concurrency limits, per the plan: OCR/unpacking is CPU/disk-bound, Gemini extraction is externally
/// rate-limited — sharing one limit between them would starve one or the other under load.</summary>
public class FilingProcessingWorker(
    IServiceScopeFactory scopeFactory,
    FilingProcessingQueue queue,
    ILogger<FilingProcessingWorker> logger,
    IOptions<LargeArchiveUploadOptions>? options = null) : BackgroundService
{
    private readonly SemaphoreSlim _documentConcurrency = new(4);
    private readonly SemaphoreSlim _extractionConcurrency = new(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        await foreach (var item in queue.ReadAllAsync(stoppingToken))
        {
            _ = HandleItemAsync(item, stoppingToken);
        }
    }

    private async Task RunStartupRecoveryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var finalizationService = scope.ServiceProvider.GetRequiredService<FinalizationRecoveryService>();
            var recoveredFinalizations = await finalizationService.ReconcileIncompleteFinalizationsAsync(ct);
            if (recoveredFinalizations > 0)
                logger.LogInformation("Reconciled {Count} interrupted large archive finalizations on startup", recoveredFinalizations);

            var reservationManager = scope.ServiceProvider.GetRequiredService<IStorageReservationManager>();
            await reservationManager.SweepExpiredReservationsAsync(ct);

            var processor = scope.ServiceProvider.GetRequiredService<FilingBatchProcessor>();
            var recovered = await processor.RecoverStaleWorkAsync(ct);
            if (recovered > 0)
                logger.LogInformation("Recovered {Count} stale MCA filing work item(s) on startup", recovered);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCA filings startup recovery sweep failed");
        }
    }

    private async Task HandleItemAsync(FilingWorkItem item, CancellationToken ct)
    {
        var semaphore = item is ExtractFilingWorkItem ? _extractionConcurrency : _documentConcurrency;
        await semaphore.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<FilingBatchProcessor>();
            switch (item)
            {
                case UnpackBatchWorkItem u: await processor.UnpackBatchAsync(u.BatchId, ct); break;
                case ProcessDocumentWorkItem p: await processor.ProcessDocumentAsync(p.FilingDocumentId, ct); break;
                case ExtractFilingWorkItem e: await processor.ExtractFilingAsync(e.FilingId, ct); break;
            }
        }
        catch (OperationalSlotBusyException busyEx) when (item is UnpackBatchWorkItem unpackItem)
        {
            var baseDelay = options?.Value?.SlotRetryDelay ?? TimeSpan.FromSeconds(5);
            var maxDelay = options?.Value?.MaxSlotRetryDelay ?? TimeSpan.FromSeconds(30);
            var nextRetry = unpackItem.RetryCount + 1;
            var delayMs = Math.Min(baseDelay.TotalMilliseconds * Math.Pow(1.5, Math.Min(unpackItem.RetryCount, 6)), maxDelay.TotalMilliseconds);
            var delay = TimeSpan.FromMilliseconds(delayMs);

            logger.LogInformation(
                "Operational slot '{SlotType}' is busy (held by {HolderId}); standing down unpack for batch {BatchId} (attempt {Attempt}), re-scheduling in {DelayMs}ms",
                busyEx.SlotType, busyEx.ActiveHolderId, unpackItem.BatchId, nextRetry, (int)delay.TotalMilliseconds);

            queue.EnqueueDelayed(new UnpackBatchWorkItem(unpackItem.BatchId, nextRetry), delay, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing MCA filing work item {ItemType}", item.GetType().Name);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
