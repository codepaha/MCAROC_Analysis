using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Every <see cref="ReferenceToolOptions.RefreshPollMinutes"/>: for each company with parked
/// (<see cref="AutoFetchJobStatus.WaitingForRefresh"/>) jobs, polls its refresh once and, as soon as the answer
/// is anything but "still waiting", re-queues those jobs — <see cref="AutoFetchJobService.ProcessAsync"/>
/// re-evaluates the gate itself and exports, fails, or defers accordingly. The pending state lives in the
/// database, so a restart loses nothing: startup recovery re-queues parked jobs and they simply re-join.</summary>
public sealed class CompanyRefreshWorker(IServiceScopeFactory scopes, AutoFetchQueue queue, IOptions<ReferenceToolOptions> options, ILogger<CompanyRefreshWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.RefreshPollMinutes)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await PollOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Company refresh poll tick failed");
            }
        }
    }

    public async Task<int> PollOnceAsync(CancellationToken ct)
    {
        List<(string Cin, string Bid)> companies;
        using (var scope = scopes.CreateScope())
        {
            var health = scope.ServiceProvider.GetRequiredService<IIntegrationHealthService>();
            if (await health.IsOpenAsync(IntegrationName.ReferenceTool, ct)) return 0;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            companies = (await db.AutoFetchJobs.AsNoTracking()
                    .Where(j => j.Status == AutoFetchJobStatus.WaitingForRefresh)
                    .Select(j => new { j.Cin, j.Bid }).Distinct().ToListAsync(ct))
                .Select(c => (c.Cin, c.Bid)).ToList();
        }

        var requeued = 0;
        foreach (var (cin, bid) in companies)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var gate = await scope.ServiceProvider.GetRequiredService<CompanyRefreshService>().EvaluateAsync(cin, bid, ct);
                if (gate.Kind == RefreshGateKind.Waiting) continue;

                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var ids = await db.AutoFetchJobs.Where(j => j.Cin == cin && j.Status == AutoFetchJobStatus.WaitingForRefresh)
                    .Select(j => j.AutoFetchJobId).ToListAsync(ct);
                foreach (var id in ids)
                {
                    var moved = await db.AutoFetchJobs.Where(j => j.AutoFetchJobId == id && j.Status == AutoFetchJobStatus.WaitingForRefresh)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(j => j.Status, AutoFetchJobStatus.Queued)
                            .SetProperty(j => j.StatusMessage, "Refresh finished — resuming.")
                            .SetProperty(j => j.HeartbeatUtc, DateTime.UtcNow), ct);
                    if (moved == 1)
                    {
                        queue.Enqueue(id);
                        requeued++;
                    }
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Polling the reference-tool refresh for {Cin} failed; will retry next tick", cin);
            }
        }
        return requeued;
    }
}
