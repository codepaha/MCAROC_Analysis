using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MCAROC_Analysis.Services.Hosting;

/// <summary>Runs a <see cref="BackgroundService"/> and starts a fresh instance of it, after a growing wait, whenever its <c>ExecuteAsync</c> faults.
/// By default a faulted worker either stops the whole host (the default) or, with the host's exception behaviour set to Ignore, stays dead until the
/// next restart while the portal keeps accepting work for it. A worker that returns normally is left finished: that is its own decision.</summary>
public sealed class SupervisedBackgroundService(Type workerType, IServiceProvider services, ILogger<SupervisedBackgroundService> logger,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IHostedService
{
    public static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    /// <summary>A run that lasted this long before faulting was healthy, so the next failure starts the backoff over.</summary>
    public static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => SuperviseAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null || _loop is null) return;
        await _cts.CancelAsync();
        try { await _loop.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { }
    }

    private async Task SuperviseAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            BackgroundService? worker = null;
            var startedUtc = DateTime.UtcNow;
            try
            {
                worker = (BackgroundService)ActivatorUtilities.CreateInstance(services, workerType);
                await worker.StartAsync(ct);
                await (worker.ExecuteTask ?? Task.CompletedTask).WaitAsync(ct);
                return; // finished by design
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (worker is not null) { try { await worker.StopAsync(CancellationToken.None); } catch { } }
                return;
            }
            catch (Exception ex)
            {
                failures = DateTime.UtcNow - startedUtc >= HealthyRun ? 1 : failures + 1;
                logger.LogError(ex, "Background worker {Worker} faulted (failure {Failures}); restarting in {Wait}", workerType.Name, failures, BackoffFor(failures));
                try { worker?.Dispose(); } catch { }
            }
            try { await _delay(BackoffFor(failures), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal static TimeSpan BackoffFor(int failures) =>
        TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, MinBackoff.TotalSeconds * Math.Pow(2, Math.Max(0, failures - 1))));
}

public static class SupervisedBackgroundServiceExtensions
{
    /// <summary>Wraps every <see cref="BackgroundService"/> registered by type in a <see cref="SupervisedBackgroundService"/>. Call after all
    /// <c>AddHostedService</c> registrations. Other hosted services are left as they are.</summary>
    public static IServiceCollection SuperviseBackgroundServices(this IServiceCollection services)
    {
        var targets = services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType is { } t
            && typeof(BackgroundService).IsAssignableFrom(t) && !typeof(SupervisedBackgroundService).IsAssignableFrom(t)).ToList();
        foreach (var d in targets)
        {
            var type = d.ImplementationType!;
            var index = services.IndexOf(d);
            services.RemoveAt(index);
            services.Insert(index, ServiceDescriptor.Singleton<IHostedService>(sp =>
                new SupervisedBackgroundService(type, sp, sp.GetRequiredService<ILogger<SupervisedBackgroundService>>())));
        }
        return services;
    }
}
