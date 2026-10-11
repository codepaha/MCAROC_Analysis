using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginLitigationTargetWorker(
    IServiceScopeFactory scopes,
    PreLoginLitigationTargetQueue queue,
    ILogger<PreLoginLitigationTargetWorker> logger) : BackgroundService
{
    private const int MaxConcurrentTargets = 5;
    private readonly SemaphoreSlim concurrency = new(MaxConcurrentTargets, MaxConcurrentTargets);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var targetId in queue.ReadAllAsync(stoppingToken))
        {
            await concurrency.WaitAsync(stoppingToken);
            _ = RunAsync(targetId, stoppingToken);
        }
    }

    private async Task RunAsync(long targetId, CancellationToken token)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<PreLoginLitigationTargetService>();
            await service.ProcessAsync(targetId, token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled pre-login litigation target failure for {TargetId}", targetId);
        }
        finally
        {
            concurrency.Release();
        }
    }
}

