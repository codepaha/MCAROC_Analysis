namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Dequeues #164 AI-audit work and dispatches it to a scoped CalculationAiAuditOrchestrator.
/// Mirrors AnalysisWorker exactly, including the concurrency limit of 2 — the same externally-rate-limited-
/// by-Vertex-AI rationale.</summary>
public class CalculationAiAuditWorker(
    IServiceScopeFactory scopeFactory,
    CalculationAiAuditQueue queue,
    ILogger<CalculationAiAuditWorker> logger) : BackgroundService
{
    private readonly SemaphoreSlim _concurrency = new(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RunStartupRecoveryAsync(stoppingToken);

        await foreach (var auditRunId in queue.ReadAllAsync(stoppingToken))
        {
            _ = HandleItemAsync(auditRunId, stoppingToken);
        }
    }

    private async Task RunStartupRecoveryAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<CalculationAiAuditOrchestrator>();
            var recovered = await orchestrator.RecoverStaleWorkAsync(ct);
            if (recovered > 0)
                logger.LogInformation("Recovered {Count} stale AI audit work item(s) on startup", recovered);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Calculation-assurance AI audit startup recovery sweep failed");
        }
    }

    private async Task HandleItemAsync(long auditRunId, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<CalculationAiAuditOrchestrator>();
            await orchestrator.RunAuditAsync(auditRunId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error processing AI audit run {AuditRunId}", auditRunId);
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
