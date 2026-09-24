using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Attaches a <see cref="PipelineRun"/> to a request: eagerly from the create paths (auto-fetch,
/// manual upload), and via a sweep for requests created on/after <c>Pipeline:AdoptAfterUtc</c>. Inert while
/// <c>Pipeline:Enabled</c> is false.</summary>
public sealed class PipelineAdopter(AppDbContext db, IOptionsMonitor<PipelineOptions> options, TimeProvider time, ILogger<PipelineAdopter> logger)
{
    private static readonly PipelineOutcome[] LiveOutcomes = [PipelineOutcome.InProgress, PipelineOutcome.CoreReady, PipelineOutcome.NeedsAttention];

    /// <summary>For the create paths: the coordinator must never break the user's own request, so any failure
    /// is logged and left for the sweep.</summary>
    public async Task TryAdoptAsync(long requestId, PipelineRunTrigger trigger, string? correlationId, CancellationToken ct)
    {
        try
        {
            await EnsureRunAsync(requestId, trigger, correlationId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not attach a pipeline run to request {RequestId}; the adoption sweep will retry.", requestId);
        }
    }

    /// <summary>Returns the request's live run, creating one if there is none; null while disabled.</summary>
    public async Task<long?> EnsureRunAsync(long requestId, PipelineRunTrigger trigger, string? correlationId, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled) return null;

        var live = await LiveRunIdAsync(requestId, ct);
        if (live is not null) return live;

        var run = new PipelineRun
        {
            RequestId = requestId,
            Trigger = trigger,
            PolicyJson = JsonSerializer.Serialize(opts.Policy),
            Outcome = PipelineOutcome.InProgress,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId.Trim(),
            CreatedUtc = time.GetUtcNow().UtcDateTime
        };
        db.PipelineRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
            return run.PipelineRunId;
        }
        catch (DbUpdateException)
        {
            // Lost the race on the one-live-run-per-request index.
            db.Entry(run).State = EntityState.Detached;
            return await LiveRunIdAsync(requestId, ct);
        }
    }

    /// <summary>Adopts requests created on/after the cutoff that have never had a run. A request whose run
    /// already finished is not re-adopted.</summary>
    public async Task<int> AdoptSweepAsync(int max, CancellationToken ct)
    {
        var opts = options.CurrentValue;
        if (!opts.Enabled || opts.AdoptAfterUtc is not { } configured) return 0;
        // The configuration binder turns "…Z" into server-local time; CreatedDate is UTC.
        var cutoff = configured.Kind == DateTimeKind.Local ? configured.ToUniversalTime() : configured;

        var candidates = await db.Requests.AsNoTracking()
            .Where(r => r.CreatedDate >= cutoff && !db.PipelineRuns.Any(p => p.RequestId == r.RequestId))
            .OrderBy(r => r.RequestId)
            .Select(r => new
            {
                r.RequestId,
                JobCorrelationId = db.AutoFetchJobs.Where(j => j.RequestId == r.RequestId).Select(j => j.CorrelationId).FirstOrDefault()
            })
            .Take(max)
            .ToListAsync(ct);

        var adopted = 0;
        foreach (var c in candidates)
            if (await EnsureRunAsync(c.RequestId, PipelineRunTrigger.Adopted, c.JobCorrelationId, ct) is not null)
                adopted++;
        return adopted;
    }

    private Task<long?> LiveRunIdAsync(long requestId, CancellationToken ct) =>
        db.PipelineRuns.AsNoTracking()
            .Where(r => r.RequestId == requestId && LiveOutcomes.Contains(r.Outcome))
            .Select(r => (long?)r.PipelineRunId)
            .FirstOrDefaultAsync(ct);
}
