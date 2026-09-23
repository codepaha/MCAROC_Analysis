using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Every <see cref="ReferenceToolOptions.RefreshPollMinutes"/>: for each company with parked jobs
/// (<see cref="AutoFetchJobStatus.WaitingForRefresh"/> or <see cref="AutoFetchJobStatus.WaitingForUnlock"/>),
/// lets <see cref="CompanyGateCoordinator"/> poll the refresh, spend an approved unlock, and re-queue whatever can
/// move on. The waiting state lives in the database, so a restart loses nothing: startup recovery re-queues
/// parked jobs and they simply re-park or proceed.</summary>
public sealed class CompanyRefreshWorker(IServiceScopeFactory scopes, IOptions<ReferenceToolOptions> options, ILogger<CompanyRefreshWorker> logger) : BackgroundService
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
                logger.LogError(ex, "Company refresh/unlock poll tick failed");
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
                    .Where(j => j.Status == AutoFetchJobStatus.WaitingForRefresh || j.Status == AutoFetchJobStatus.WaitingForUnlock)
                    .Select(j => new { j.Cin, j.Bid }).Distinct().ToListAsync(ct))
                .Select(c => (c.Cin, c.Bid)).ToList();
        }

        var requeued = 0;
        foreach (var (cin, bid) in companies)
        {
            try
            {
                using var scope = scopes.CreateScope();
                requeued += await scope.ServiceProvider.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(cin, bid, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Polling the reference-tool gate for {Cin} failed; will retry next tick", cin);
            }
        }
        return requeued;
    }
}
