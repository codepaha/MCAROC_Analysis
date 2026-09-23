using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Resolves outstanding <c>Reserved</c> admissions on a timer so a scope is never held forever by a
/// crashed caller. Resolution is idempotent (every transition is conditional on <c>State = Reserved</c>), so
/// this is safe to run on every instance alongside the eager resolution the start paths do themselves.</summary>
public sealed class PaidCallAdmissionSweepWorker(IServiceScopeFactory scopes, IOptions<PipelineOptions> options, ILogger<PaidCallAdmissionSweepWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.AdmissionSweepMinutes)));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var resolved = await scope.ServiceProvider.GetRequiredService<IPaidCallAdmission>()
                    .ResolveOutstandingAsync(kind: null, requestId: null, stoppingToken);
                if (resolved > 0) logger.LogInformation("Paid-call admission sweep resolved {Count} reservation(s)", resolved);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Paid-call admission sweep tick failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
