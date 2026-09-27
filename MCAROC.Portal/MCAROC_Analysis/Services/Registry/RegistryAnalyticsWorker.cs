using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Registry;

/// <summary>Refreshes imported analytics in an owned scope; HTTP requests only read cached aggregates.</summary>
public sealed class RegistryAnalyticsWorker(IServiceScopeFactory scopes, ILogger<RegistryAnalyticsWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<CompanyRegistryQueryService>().RefreshImportedBaselineAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning(ex, "Imported registry analytics refresh failed; retaining the last calculated baseline."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
