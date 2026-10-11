using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Bridge;

/// <summary>Applies the bridge outbox in the background while <c>LitigationModule:Bridge:Enabled</c> is true. It does nothing otherwise. Each pass is one batch; the
/// processor is idempotent, so a crash or a second instance cannot corrupt anything, only repeat work.</summary>
public sealed class LitigationBridgeWorker(IServiceScopeFactory scopes, IOptions<LitigationBridgeOptions> options, ILogger<LitigationBridgeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MCAROC_Analysis.Data.AppDbContext>();
                var processor = new LitigationBridgeProcessor(db, opts);
                var result = await processor.ProcessBatchAsync(stoppingToken);
                if (result.DeadLettered > 0)
                    logger.LogError("Litigation bridge dead-lettered {Count} event(s); they stay in the outbox for inspection.", result.DeadLettered);
                var lag = await processor.GetLagAsync(stoppingToken);
                if (lag.Pending > opts.SyncingBacklogThreshold)
                    logger.LogWarning("Litigation bridge backlog is {Pending} event(s), oldest {Age}.", lag.Pending, lag.OldestPendingAge);
                if (result.Applied + result.Failed > 0 && lag.Pending > 0) continue;   // more waiting: do not sleep
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { logger.LogError(ex, "Litigation bridge pass failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
