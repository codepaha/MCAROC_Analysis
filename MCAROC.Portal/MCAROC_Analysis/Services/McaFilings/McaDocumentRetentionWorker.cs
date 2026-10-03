using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.McaFilings;

public sealed class McaDocumentRetentionWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<McaDocumentRetentionWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var retentionService = scope.ServiceProvider.GetRequiredService<McaDocumentRetentionService>();

                var prunedDocs = await retentionService.PruneExpiredDocumentsAsync(stoppingToken);
                var prunedChunks = await retentionService.PruneSupersededBatchChunksAsync(stoppingToken);

                if (prunedDocs > 0 || prunedChunks > 0)
                {
                    logger.LogInformation("Document retention sweep completed: {Docs} PDFs pruned, {Chunks} superseded chunks deleted.", prunedDocs, prunedChunks);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error in McaDocumentRetentionWorker execution.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}