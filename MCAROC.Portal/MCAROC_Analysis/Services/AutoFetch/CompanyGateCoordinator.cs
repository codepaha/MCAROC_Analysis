using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>For one company, moves its parked auto-fetch jobs on once the unlock/refresh gate allows:
/// <list type="bullet">
/// <item>Still locked → jobs waiting for a refresh are re-queued (they'll re-park for the unlock), and if any job
/// is waiting for the unlock, <see cref="CompanyUnlockService"/> tries to spend its approval. Unlocked (by us or
/// anyone) → those jobs resume; an identity mismatch or a failed paid call fails them (never retried
/// automatically); no approval yet → they keep waiting.</item>
/// <item>Refresh still pending → only jobs that were waiting for the unlock are re-queued, to join it.</item>
/// <item>Anything else (ready, timed out, deferred) → every parked job is re-queued; <see
/// cref="AutoFetchJobService.ProcessAsync"/> re-evaluates the gate itself and acts on the answer.</item>
/// </list>
/// Used by <see cref="CompanyRefreshWorker"/> on its poll and by the approve action, so an approval takes effect
/// immediately rather than on the next tick. Every state change is a conditional update on the job's parked
/// status, so the two can overlap safely.</summary>
public sealed class CompanyGateCoordinator(
    AppDbContext db, CompanyRefreshService refresh, CompanyUnlockService unlock, AutoFetchQueue queue, ILogger<CompanyGateCoordinator> logger)
{
    public async Task<int> ResumeAsync(string cin, string bid, CancellationToken ct)
    {
        var gate = await refresh.EvaluateAsync(cin, bid, ct);
        switch (gate.Kind)
        {
            case RefreshGateKind.Waiting:
                return await RequeueAsync(cin, AutoFetchJobStatus.WaitingForUnlock, ct);

            case RefreshGateKind.Locked:
            {
                var requeued = await RequeueAsync(cin, AutoFetchJobStatus.WaitingForRefresh, ct);
                var waiting = await db.AutoFetchJobs.AsNoTracking()
                    .Where(j => j.Cin == cin && j.Status == AutoFetchJobStatus.WaitingForUnlock)
                    .OrderBy(j => j.AutoFetchJobId).Select(j => (long?)j.RequestId).FirstOrDefaultAsync(ct);
                if (waiting is not { } requestId) return requeued;

                var result = await unlock.ExecuteAsync(cin, bid, requestId, ct);
                switch (result.Outcome)
                {
                    case UnlockOutcome.Unlocked:
                    case UnlockOutcome.AlreadyUnlocked:
                        logger.LogInformation("{Cin}: {Message}", cin, result.Message);
                        return requeued + await RequeueAsync(cin, AutoFetchJobStatus.WaitingForUnlock, ct);
                    case UnlockOutcome.IdentityMismatch:
                    case UnlockOutcome.Failed:
                        await FailAsync(cin, AutoFetchJobStatus.WaitingForUnlock, result.Message, ct);
                        return requeued;
                    default:
                        await db.AutoFetchJobs.Where(j => j.Cin == cin && j.Status == AutoFetchJobStatus.WaitingForUnlock && j.StatusMessage != result.Message)
                            .ExecuteUpdateAsync(s => s.SetProperty(j => j.StatusMessage, Trim(result.Message)), ct);
                        return requeued;
                }
            }

            default:
                return await RequeueAsync(cin, AutoFetchJobStatus.WaitingForRefresh, ct)
                     + await RequeueAsync(cin, AutoFetchJobStatus.WaitingForUnlock, ct);
        }
    }

    private async Task<int> RequeueAsync(string cin, AutoFetchJobStatus parked, CancellationToken ct)
    {
        var ids = await db.AutoFetchJobs.Where(j => j.Cin == cin && j.Status == parked).Select(j => j.AutoFetchJobId).ToListAsync(ct);
        var requeued = 0;
        foreach (var id in ids)
        {
            var moved = await db.AutoFetchJobs.Where(j => j.AutoFetchJobId == id && j.Status == parked)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, AutoFetchJobStatus.Queued)
                    .SetProperty(j => j.StatusMessage, "Resuming.")
                    .SetProperty(j => j.HeartbeatUtc, DateTime.UtcNow), ct);
            if (moved != 1) continue;
            queue.Enqueue(id);
            requeued++;
        }
        return requeued;
    }

    private Task FailAsync(string cin, AutoFetchJobStatus parked, string reason, CancellationToken ct) =>
        db.AutoFetchJobs.Where(j => j.Cin == cin && j.Status == parked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, AutoFetchJobStatus.Failed)
                .SetProperty(j => j.FailureReason, reason)
                .SetProperty(j => j.StatusMessage, "Failed.")
                .SetProperty(j => j.CompletedUtc, DateTime.UtcNow)
                .SetProperty(j => j.HeartbeatUtc, DateTime.UtcNow), ct);

    private static string Trim(string message) => message.Length > 500 ? message[..500] : message;
}
