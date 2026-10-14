using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

public sealed class LitigationAttributionOptions
{
    public const string SectionName = "LitigationAttribution";
    /// <summary>On by default: the pipeline's litigation stage waits for attribution (#489, decision a).</summary>
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int RequestsPerPass { get; set; } = 5;
    /// <summary>A request whose only outstanding cases wait for a judgement that failed is tried again after this long, not every pass.</summary>
    public int PendingRetryMinutes { get; set; } = 10;
}

/// <summary>Attributes the cases of every request that has some not yet settled: a new import, a refresh, the cases stored before attribution existed, and
/// judgements that failed. Requests with never-assessed cases go first. A failed pass is logged and the next one tries again.</summary>
public sealed class LitigationAttributionWorker(IServiceScopeFactory scopes, IOptions<LitigationAttributionOptions> options, ILogger<LitigationAttributionWorker> logger) : BackgroundService
{
    private readonly Dictionary<long, DateTime> _lastPendingAttemptUtc = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                foreach (var requestId in await DueRequestsAsync(db, opts, stoppingToken))
                {
                    using var requestScope = scopes.CreateScope();
                    await requestScope.ServiceProvider.GetRequiredService<LitigationAttributionService>().AttributeRequestAsync(requestId, stoppingToken);
                    _lastPendingAttemptUtc[requestId] = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Litigation attribution pass failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, opts.PollSeconds)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<List<long>> DueRequestsAsync(AppDbContext db, LitigationAttributionOptions opts, CancellationToken ct)
    {
        var unassessed = await db.LitigationCases.AsNoTracking().Where(c => c.MergedIntoCaseId == null && c.AttributionOutcome == null)
            .Select(c => c.RequestId).Distinct().Take(opts.RequestsPerPass).ToListAsync(ct);
        if (unassessed.Count >= opts.RequestsPerPass) return unassessed;

        var retryBefore = DateTime.UtcNow.AddMinutes(-Math.Max(1, opts.PendingRetryMinutes));
        var pending = await db.LitigationCases.AsNoTracking().Where(c => c.MergedIntoCaseId == null && c.AttributionOutcome == LitigationAttributionOutcome.Pending)
            .Select(c => c.RequestId).Distinct().ToListAsync(ct);
        return unassessed.Concat(pending.Where(r => !unassessed.Contains(r) && (!_lastPendingAttemptUtc.TryGetValue(r, out var at) || at < retryBefore)))
            .Take(opts.RequestsPerPass).ToList();
    }
}
