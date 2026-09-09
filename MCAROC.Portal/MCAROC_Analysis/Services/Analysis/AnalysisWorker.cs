namespace MCAROC_Analysis.Services.Analysis;

/// <summary>Dequeues analysis work and dispatches it to a scoped AnalysisOrchestrator. Concurrency limited
/// to 2 — the same rationale as Phase 2's Gemini-extraction limit: externally rate-limited by Vertex AI.</summary>
public class AnalysisWorker(
    IServiceScopeFactory scopeFactory,
    AnalysisQueue queue,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        await foreach (var requestId in queue.ReadAllAsync(stoppingToken))
        {
            _ = HandleItemAsync(requestId, stoppingToken);
        }
    }

    private async Task RunStartupRecoveryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AnalysisOrchestrator>();
            var recovered = await orchestrator.RecoverStaleWorkAsync(ct);
            if (recovered > 0)
                logger.LogInformation("Recovered {Count} stale analysis work item(s) on startup", recovered);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Analysis startup recovery sweep failed");
        }
    }

    private async Task HandleItemAsync(long requestId, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<AnalysisOrchestrator>();
            await orchestrator.RunAnalysisAsync(requestId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing analysis for request {RequestId}", requestId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
