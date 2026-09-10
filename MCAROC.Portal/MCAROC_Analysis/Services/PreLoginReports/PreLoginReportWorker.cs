using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginReportWorker(IServiceScopeFactory scopes, PreLoginReportQueue queue, ILogger<PreLoginReportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        await foreach (var jobId in queue.ReadAllAsync(stoppingToken)) _ = RunAsync(jobId, stoppingToken);
    }
    private async Task RecoverAsync(CancellationToken token)
    {
        using var scope = scopes.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var ids = await db.PreLoginReportJobs.Where(x => x.Status == PreLoginReportJobStatus.Queued || x.Status == PreLoginReportJobStatus.Fetching || x.Status == PreLoginReportJobStatus.Generating).Select(x => x.PreLoginReportJobId).ToListAsync(token);
        foreach (var id in ids) queue.Enqueue(id);
    }
    private async Task RunAsync(long jobId, CancellationToken token)
    {
        try { using var scope = scopes.CreateScope(); await scope.ServiceProvider.GetRequiredService<PreLoginReportJobService>().ProcessAsync(jobId, token); }
        catch (Exception ex) { logger.LogError(ex, "Unhandled pre-login report job failure for {JobId}", jobId); }
    }
}
