using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Half-open probe for the reference-tool breaker (docs/pipeline-automation-plan.md §5.4). Ticks
/// every <see cref="ReferenceToolOptions.HealthProbeMinutes"/>; on every tick it is a no-op unless the
/// breaker is currently <see cref="IntegrationHealthState.Open"/> — ordinary calls made by <see
/// cref="AutoFetchJobService"/> already report success/failure via <see
/// cref="ReferenceToolClient"/>'s own recovery wrapper, so this class exists only to attempt the one
/// cheap call that can close an open breaker back up, which no ordinary in-flight caller is allowed to do.</summary>
public sealed class ReferenceToolHealthProbe(IServiceScopeFactory scopes, IOptions<ReferenceToolOptions> options, ILogger<ReferenceToolHealthProbe> logger) : BackgroundService
{
    private readonly ReferenceToolOptions _opts = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _opts.HealthProbeMinutes));
        using var timer = new PeriodicTimer(interval);
        do
        {
            await ProbeOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();
            if (!await health.IsOpenAsync(IntegrationName.ReferenceTool, ct)) return;

            var probeLease = TimeSpan.FromMinutes(Math.Max(1, _opts.BreakerProbeLeaseMinutes));
            if (!await health.TryClaimHalfOpenProbeAsync(IntegrationName.ReferenceTool, probeLease, ct))
                return; // another instance/tick already owns the current probe window

            var client = scope.ServiceProvider.GetRequiredService<ReferenceToolClient>();
            if (!client.IsConfigured) return;

            var callStartUtc = DateTime.UtcNow;
            try
            {
                var session = await client.CheckSessionAsync(ct);
                if (session.IsValid)
                {
                    await health.TryCloseAfterProbeAsync(IntegrationName.ReferenceTool, callStartUtc, ct);
                    logger.LogInformation("Reference-tool breaker closed after a successful probe");
                }
                else
                {
                    await health.ReportFailureAsync(IntegrationName.ReferenceTool, callStartUtc, session.Detail,
                        session.Kind == ReferenceToolFailureKind.AuthRejected, _opts.BreakerThreshold, probeLease, ct);
                }
            }
            catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await health.ReportFailureAsync(IntegrationName.ReferenceTool, callStartUtc, ex.Message,
                    ex is ReferenceToolException { Kind: ReferenceToolFailureKind.AuthRejected }, _opts.BreakerThreshold, probeLease, ct);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Reference-tool health probe tick failed");
        }
    }
}
