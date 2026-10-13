using System.Threading;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>Dequeues MCA Filings work and dispatches it to a scoped FilingBatchProcessor. Three separate,
/// independently configurable concurrency limits (LargeArchiveUploadOptions — defaults match the values
/// this class hardcoded before): unpacking (bounded by MaxConcurrentUnpacks — also the batch-wide cap
/// OperationalSlotLeaseService enforces, so several outer archives can genuinely unpack at once instead of
/// unpack work starving document OCR or vice versa by sharing one semaphore with it), OCR/classification
/// (CPU/disk-bound), and Gemini extraction (externally rate-limited by the Vertex AI project quota — raise
/// this only after checking that quota, not just local CPU headroom).</summary>
public class FilingProcessingWorker(
    IServiceScopeFactory scopeFactory,
    FilingProcessingQueue queue,
    ILogger<FilingProcessingWorker> logger,
    IOptions<LargeArchiveUploadOptions>? options = null) : BackgroundService
{
    /// <summary>Workers per kind of work. Each worker takes one item at a time from its kind's queue, which serves batches round-robin, so
    /// the worker count is the concurrency and no batch waits behind another.</summary>
    internal static (int Unpack, int Document, int Extraction) WorkerCounts(LargeArchiveUploadOptions? o) => (
        Math.Max(1, o?.MaxConcurrentUnpacks ?? 1),
        LargeArchiveUploadOptions.EffectiveDocumentConcurrency(o?.MaxConcurrentDocumentProcessing ?? 4),
        Math.Max(1, o?.MaxConcurrentAiExtraction ?? 2));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        var (unpack, document, extraction) = WorkerCounts(options?.Value);
        logger.LogInformation("MCA filings workers: {Unpack} unpack, {Document} document, {Extraction} AI extraction (served round-robin across batches)",
            unpack, document, extraction);
        var workers = new List<Task>();
        workers.AddRange(Enumerable.Range(0, unpack).Select(_ => RunWorkerAsync(FilingWorkKind.Unpack, stoppingToken)));
        workers.AddRange(Enumerable.Range(0, document).Select(_ => RunWorkerAsync(FilingWorkKind.Document, stoppingToken)));
        workers.AddRange(Enumerable.Range(0, extraction).Select(_ => RunWorkerAsync(FilingWorkKind.Extraction, stoppingToken)));
        await Task.WhenAll(workers);
    }

    private async Task RunWorkerAsync(FilingWorkKind kind, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            FilingWorkItem item;
            try { item = await queue.DequeueAsync(kind, ct); }
            catch (OperationCanceledException) { return; }
            await HandleItemAsync(item, ct);
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

            queue.EnqueueDelayed(new UnpackBatchWorkItem(unpackItem.BatchId, nextRetry) { Lane = unpackItem.BatchId }, delay, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing MCA filing work item {ItemType}", item.GetType().Name);
        }
    }
}
