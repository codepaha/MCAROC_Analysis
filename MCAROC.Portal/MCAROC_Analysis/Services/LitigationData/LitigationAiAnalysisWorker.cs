namespace MCAROC_Analysis.Services.LitigationData;

public sealed class LitigationAiAnalysisWorker(IServiceScopeFactory scopes, LitigationAiAnalysisQueue queue,
    ILogger<LitigationAiAnalysisWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(2);
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var count = await scope.ServiceProvider.GetRequiredService<LitigationAiAnalysisOrchestrator>().RecoverStaleWorkAsync(stoppingToken);
            if (count > 0) logger.LogInformation("Recovered {Count} litigation AI analysis item(s)", count);
        }
        catch (Exception ex) { logger.LogError(ex, "Litigation AI analysis startup recovery failed"); }
        await foreach (var runId in queue.ReadAllAsync(stoppingToken)) _ = HandleAsync(runId, stoppingToken);
    }
    private async Task HandleAsync(long runId, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<LitigationAiAnalysisOrchestrator>().RunAsync(runId, ct); }
        catch (Exception ex) { logger.LogError(ex, "Unhandled litigation AI analysis failure for run {RunId}", runId); }
        finally { _concurrency.Release(); }
    }
}
