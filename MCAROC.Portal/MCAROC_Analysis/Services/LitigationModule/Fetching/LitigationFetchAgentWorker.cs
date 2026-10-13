using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

public sealed class FetchAgentOptions
{
    public const string SectionName = "LitigationPortals:Agents";

    /// <summary>Off by default: no court portal is called until this is switched on per environment.</summary>
    public bool Enabled { get; set; }
    /// <summary>Agents per portal in this process. The portal's total load is capped by the traffic gate whatever this is (plan 4.4 flow 5).</summary>
    public int AgentsPerHost { get; set; } = 1;
    /// <summary>How long an agent sleeps when there is nothing due.</summary>
    public int IdleSeconds { get; set; } = 15;
    /// <summary>How often Failed items past their cool-down are queued again (plan 4.9 §4).</summary>
    public int RequeueFailedMinutes { get; set; } = 60;
}

/// <summary>Runs the fetch agents in the background while <c>LitigationPortals:Agents:Enabled</c> is true; does nothing otherwise. Each agent turn uses its own scope (its own database
/// context), so agents never share tracked state. A turn that fails is logged and the agent carries on: its item comes back through the lease.</summary>
public sealed class LitigationFetchAgentWorker(
    IServiceScopeFactory scopes, IOptions<FetchAgentOptions> options, ILogger<LitigationFetchAgentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        var agents = new List<Task>();
        foreach (var host in new[] { FetchHost.Drt, FetchHost.Consumer })
            for (var i = 1; i <= Math.Max(1, opts.AgentsPerHost); i++)
                agents.Add(RunAgentAsync(host, $"{Environment.MachineName}:{host}:{i}", opts, stoppingToken));
        agents.Add(RequeueFailedAsync(opts, stoppingToken));
        agents.Add(CloseFinishedRunsAsync(opts, stoppingToken));
        await Task.WhenAll(agents);
    }

    private async Task RunAgentAsync(FetchHost host, string agentId, FetchAgentOptions opts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var turn = AgentTurn.Idle;
            try
            {
                using var scope = scopes.CreateScope();
                turn = await scope.ServiceProvider.GetRequiredService<FetchAgent>().RunOnceAsync(host, agentId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Fetch agent {Agent} turn failed.", agentId); }
            if (turn is AgentTurn.Idle or AgentTurn.Deferred)
            {
                // Deferred means the gate refused (host full or broken): every item would be refused the same way just now, so wait like an idle agent.
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, opts.IdleSeconds)), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Marks runs finished once none of their items is left to do, so the banner ends and end-of-run work can follow.</summary>
    private async Task CloseFinishedRunsAsync(FetchAgentOptions opts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var runs = scope.ServiceProvider.GetRequiredService<RefreshRunService>();
                foreach (var (runId, requestId) in await runs.OpenRunsAsync(ct))
                    if (await runs.CompleteIfDoneAsync(runId, requestId, ct))
                        logger.LogInformation("Court update run {RunId} for request {RequestId} finished.", runId, requestId);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Closing finished court update runs failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opts.IdleSeconds * 2)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RequeueFailedAsync(FetchAgentOptions opts, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var n = await scope.ServiceProvider.GetRequiredService<FetchWorkQueue>().RequeueFailedAsync(ct);
                if (n > 0) logger.LogInformation("Queued {Count} failed court fetch item(s) again after their cool-down.", n);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Re-queueing failed court fetch items failed."); }
            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.RequeueFailedMinutes)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}

public static class LitigationFetchServiceCollectionExtensions
{
    /// <summary>The fetch agents, the queue, the traffic gate and the two portal clients. The clients are singletons: one HTTP client and one proxy rotator per portal per process.</summary>
    public static IServiceCollection AddLitigationFetching(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FetchAgentOptions>(configuration.GetSection(FetchAgentOptions.SectionName));
        services.Configure<DrtClientOptions>(configuration.GetSection(DrtClientOptions.SectionName));
        services.Configure<ConsumerClientOptions>(configuration.GetSection(ConsumerClientOptions.SectionName));
        var traffic = configuration.GetSection(HostTrafficOptions.SectionName).Get<HostTrafficOptions>() ?? new HostTrafficOptions();
        services.AddSingleton(traffic);
        services.AddSingleton(new FetchQueueOptions());

        services.AddSingleton<IDrtClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<DrtClientOptions>>();
            return new DrtClient(DrtClient.CreateHttpClient(o.Value), o, sp.GetRequiredService<ILogger<DrtClient>>(), sp.GetService<TimeProvider>());
        });
        services.AddSingleton<IConsumerClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ConsumerClientOptions>>();
            return new ConsumerClient(ConsumerClient.CreateHttpClient(o.Value), o, sp.GetRequiredService<ILogger<ConsumerClient>>(), sp.GetService<TimeProvider>());
        });

        services.AddScoped(sp => new FetchWorkQueue(sp.GetRequiredService<Data.AppDbContext>(), sp.GetRequiredService<FetchQueueOptions>(), sp.GetService<TimeProvider>()));
        services.AddScoped(sp => new HostTrafficGate(sp.GetRequiredService<Data.AppDbContext>(), sp.GetRequiredService<IOperationalSlotLeaseService>(),
            sp.GetRequiredService<HostTrafficOptions>(), sp.GetService<TimeProvider>()));
        services.AddScoped(sp => new FetchAgent(sp.GetRequiredService<Data.AppDbContext>(), sp.GetRequiredService<FetchWorkQueue>(), sp.GetRequiredService<HostTrafficGate>(),
            sp.GetRequiredService<IDrtClient>(), sp.GetRequiredService<IConsumerClient>(), sp.GetRequiredService<ILogger<FetchAgent>>(), sp.GetService<TimeProvider>()));
        services.AddScoped(sp => new RefreshRunService(sp.GetRequiredService<Data.AppDbContext>(), sp.GetRequiredService<FetchWorkQueue>(), sp.GetService<TimeProvider>()));
        services.AddHostedService<LitigationFetchAgentWorker>();
        return services;
    }
}
