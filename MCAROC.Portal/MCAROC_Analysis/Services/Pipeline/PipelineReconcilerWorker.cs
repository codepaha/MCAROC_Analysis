using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Every <c>Pipeline:TickSeconds</c>: adopt new requests, then reconcile up to
/// <c>Pipeline:MaxRunsPerTick</c> due runs, each in its own scope. Safe to run on several instances — each
/// run is claimed with a fenced lease.</summary>
public sealed class PipelineReconcilerWorker(IServiceScopeFactory scopes, IOptionsMonitor<PipelineOptions> options, ILogger<PipelineReconcilerWorker> logger) : BackgroundService
{
    private const int AdoptPerTick = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, options.CurrentValue.TickSeconds)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var opts = options.CurrentValue;
            if (!opts.Enabled) continue;
            try
            {
                List<long> due;
                using (var scope = scopes.CreateScope())
                {
                    await scope.ServiceProvider.GetRequiredService<PipelineAdopter>().AdoptSweepAsync(AdoptPerTick, stoppingToken);
                    due = await scope.ServiceProvider.GetRequiredService<PipelineReconciler>().SelectDueRunIdsAsync(Math.Max(1, opts.MaxRunsPerTick), stoppingToken);
                }
                foreach (var runId in due)
                {
                    using var scope = scopes.CreateScope();
                    try
                    {
                        await scope.ServiceProvider.GetRequiredService<PipelineReconciler>().ReconcileAsync(runId, stoppingToken);
                    }
                    catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                    {
                        // The lease expires on its own; the run is retried on a later tick.
                        logger.LogError(ex, "Reconciling pipeline run {RunId} failed", runId);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Pipeline reconcile tick failed");
            }
        }
    }
}
