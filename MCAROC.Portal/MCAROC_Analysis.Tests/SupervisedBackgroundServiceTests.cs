using MCAROC_Analysis.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>A worker whose ExecuteAsync faults used to stop the host, or (with exceptions ignored) stay dead until the next restart while the portal kept
/// accepting work for it. The supervisor restarts it with a growing wait; a worker that finishes by design is left finished.</summary>
public class SupervisedBackgroundServiceTests
{
    private sealed class Counter { public int Starts; public int FaultFirst; public volatile bool Running; }

    private sealed class FlakyWorker(Counter counter) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var n = Interlocked.Increment(ref counter.Starts);
            await Task.Yield();
            if (n <= counter.FaultFirst) throw new InvalidOperationException($"database timeout #{n}");
            counter.Running = true;
            try { await Task.Delay(Timeout.Infinite, stoppingToken); } catch (OperationCanceledException) { }
        }
    }

    private sealed class OneShotWorker(Counter counter) : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) { Interlocked.Increment(ref counter.Starts); return Task.CompletedTask; }
    }

    private sealed class PlainHostedService : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (SupervisedBackgroundService Supervisor, List<TimeSpan> Waits) Create<TWorker>(Counter counter) where TWorker : BackgroundService
    {
        var services = new ServiceCollection().AddSingleton(counter).BuildServiceProvider();
        var waits = new List<TimeSpan>();
        var supervisor = new SupervisedBackgroundService(typeof(TWorker), services, NullLogger<SupervisedBackgroundService>.Instance,
            (wait, _) => { lock (waits) waits.Add(wait); return Task.CompletedTask; });
        return (supervisor, waits);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not reached"); await Task.Delay(10); }
    }

    [Fact]
    public async Task A_worker_that_faults_is_restarted_with_a_growing_wait_until_it_runs()
    {
        var counter = new Counter { FaultFirst = 3 };
        var (supervisor, waits) = Create<FlakyWorker>(counter);

        await supervisor.StartAsync(CancellationToken.None);
        await UntilAsync(() => counter.Running);
        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(4, counter.Starts);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)], waits);
    }

    [Fact]
    public async Task A_worker_that_finishes_by_design_is_not_restarted()
    {
        var counter = new Counter();
        var (supervisor, waits) = Create<OneShotWorker>(counter);

        await supervisor.StartAsync(CancellationToken.None);
        await UntilAsync(() => counter.Starts == 1);
        await Task.Delay(100);
        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(1, counter.Starts);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task The_wait_is_capped()
    {
        Assert.Equal(TimeSpan.FromSeconds(60), SupervisedBackgroundService.BackoffFor(50));
        Assert.Equal(TimeSpan.FromSeconds(2), SupervisedBackgroundService.BackoffFor(1));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Stopping_ends_a_running_worker()
    {
        var counter = new Counter();
        var (supervisor, _) = Create<FlakyWorker>(counter);
        await supervisor.StartAsync(CancellationToken.None);
        await UntilAsync(() => counter.Running);

        await supervisor.StopAsync(CancellationToken.None);

        Assert.Equal(1, counter.Starts);
    }

    [Fact]
    public void Registration_wraps_background_services_and_leaves_other_hosted_services_alone()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Counter>();
        services.AddHostedService<FlakyWorker>();
        services.AddHostedService<PlainHostedService>();
        services.AddLogging();

        services.SuperviseBackgroundServices();
        using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();

        Assert.Equal(2, hosted.Count);
        Assert.IsType<SupervisedBackgroundService>(hosted[0]);
        Assert.IsType<PlainHostedService>(hosted[1]);
    }
}
