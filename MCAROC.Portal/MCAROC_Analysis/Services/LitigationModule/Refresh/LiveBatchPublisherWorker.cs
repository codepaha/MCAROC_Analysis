using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>Runs <see cref="LiveBatchPublisher.PublishDueAsync"/> in the background while <c>LitigationModule:LivePublisher:Enabled</c> is true; does nothing otherwise.
/// A failed pass is logged and the next pass tries again: publication is idempotent.</summary>
public sealed class LiveBatchPublisherWorker(IServiceScopeFactory scopes, IOptions<LiveBatchOptions> options, ILogger<LiveBatchPublisherWorker> logger) : BackgroundService
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
                var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                var results = await new LiveBatchPublisher(db, opts, scope.ServiceProvider.GetService<TimeProvider>()).PublishDueAsync(stoppingToken);
                foreach (var r in results.Where(r => r.Published > 0))
                    logger.LogInformation("Published {Published} refreshed case(s) into request {RequestId} (version {Version}); {Unchanged} unchanged.",
                        r.Published, r.RequestId, r.NewLiveDataVersion, r.Unchanged);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Live batch publication pass failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
