using MCAROC_Analysis.Data;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

/// <summary>Where a run is (plan 4.8: the phase line under the banner).</summary>
public enum RefreshRunPhase { RefreshingCases = 1, FindingMissingCases = 2, FetchingOrders = 3, ReanalysingChangedCases = 4, Done = 9 }

/// <param name="Enqueued">Cases queued for a status fetch: the run's total for the progress bar (plan 4.8: disposed and not-due cases are not in it).</param>
/// <param name="NoLookup">Cases due for a refresh that no portal can be asked about, with the reasons and their counts.</param>
public sealed record RefreshRunStart(long RunId, bool AlreadyRunning, int Enqueued, int NotDue, int Disposed, int OrdersOnly, int NoLookup, IReadOnlyDictionary<string, int> NoLookupReasons);

public sealed record RefreshRunProgress(long RunId, long RequestId, DateTime StartedUtc, DateTime? FinishedUtc, RefreshRunPhase Phase, int EligibleAtStart,
    int Queued, int InProgress, int Succeeded, int Failed, DateTime? LastAgentHeartbeatUtc)
{
    public int Handled => Succeeded + Failed;
}

/// <summary>Starts and follows a request's court update (plan 4.4 flow 1, 4.7, 4.8). Starting decides, from data already held and before any portal call, which cases are worth a
/// call (<see cref="LiveRefreshEligibility"/>), finds a lookup for each (its shared key, or for a keyless vendor row the lookup input parsed from it) and queues one status fetch per
/// case. Nothing waits for the run: the agents work the queue, the batch publisher publishes as results land, and the run is finished when none of its items is left to do.</summary>
public sealed class RefreshRunService(AppDbContext db, FetchWorkQueue queue, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task<RefreshRunStart> StartAsync(long requestId, CancellationToken ct)
    {
        var active = await db.Set<ModuleRefreshRun>().AsNoTracking().Where(r => r.RequestId == requestId && r.FinishedUtc == null)
            .OrderByDescending(r => r.RunId).FirstOrDefaultAsync(ct);
        if (active is not null)
            return new RefreshRunStart(active.RunId, true, active.EligibleAtStart, 0, 0, 0, 0, new Dictionary<string, int>());

        var now = Now;
        var todayIst = DateOnly.FromDateTime(Ist.Now(_time));
        var run = new ModuleRefreshRun { RequestId = requestId, StartedUtc = now, Phase = (int)RefreshRunPhase.RefreshingCases };
        db.Set<ModuleRefreshRun>().Add(run);
        await db.SaveChangesAsync(ct);

        var cases = await db.LitigationCases.AsNoTracking().Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null)
            .OrderBy(c => c.LitigationCaseId).ToListAsync(ct);
        var store = new LitigationModuleStore(db, _time);
        var moduleCases = new Dictionary<long, ModuleCase>();
        foreach (var c in cases) moduleCases[c.LitigationCaseId] = await store.EnsureCaseForLegacyAsync(c.LitigationCaseId, ct);
        var moduleIds = moduleCases.Values.Select(m => m.CaseId).ToList();
        var lives = await db.Set<ModuleCaseLive>().AsNoTracking().Where(l => moduleIds.Contains(l.CaseId)).ToDictionaryAsync(l => l.CaseId, ct);

        int enqueued = 0, notDue = 0, disposed = 0, ordersOnly = 0, noLookup = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var c in cases)
        {
            var mc = moduleCases[c.LitigationCaseId];
            lives.TryGetValue(mc.CaseId, out var live);
            var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr));
            var state = new LiveRefreshCaseState(c.CaseStatus, c.CaseStage, c.Court, c.State, c.District, c.FilingDate, c.LastHearingDate, c.NextHearingDate,
                parts.Completeness, EverLiveVerified: live?.LastObservationId is not null, NewlyDiscovered: false, ConfirmedEmpty: ConfirmedEmpty(live));
            var decision = LiveRefreshEligibility.Decide(state, todayIst, now);
            switch (decision.Action)
            {
                case LiveRefreshAction.Skip:
                    if (LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage) == LitigationCaseStatusBucket.Disposed) disposed++; else notDue++;
                    continue;
                case LiveRefreshAction.FetchOrdersOnly:
                    ordersOnly++;   // the orders path has its own work kind; queued when its handler lands
                    continue;
            }
            // A keyed case is looked up by its key only; a keyless one by the lookup input parsed from its row (plan 3.3).
            string reason;
            var lookup = mc.CaseKey is not null ? PortalLookup.FromCaseKey(mc.CaseKey, out reason) : RefreshLookup.FromLegacy(c, out reason);
            if (lookup is null)
            {
                noLookup++;
                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                continue;
            }
            await queue.EnqueueAsync(FetchWorkKind.StatusFetch, lookup.Host, mc.CaseId, run.RunId, RefreshPayload.From(lookup, requestId).ToJson(), 0, ct);
            enqueued++;
        }

        await db.Set<ModuleRefreshRun>().Where(r => r.RunId == run.RunId).ExecuteUpdateAsync(u => u.SetProperty(r => r.EligibleAtStart, enqueued), ct);
        // A run with nothing to fetch is finished the moment it starts.
        if (enqueued == 0) await FinishIfDoneAsync(run.RunId, ct);
        return new RefreshRunStart(run.RunId, false, enqueued, notDue, disposed, ordersOnly, noLookup, reasons);
    }

    /// <summary>Fields a verified portal call returned empty, with when: within the confirmed-empty window they count as known, so such a case is not fetched every cycle for ever.
    /// The projection's field names and the eligibility's field names are the same words.</summary>
    private static IReadOnlyDictionary<string, DateTime>? ConfirmedEmpty(ModuleCaseLive? live) =>
        live is null ? null : LiveProjectionEngine.DeserializeSources(live.FieldSourcesJson).Where(kv => kv.Value.ConfirmedEmpty).ToDictionary(kv => kv.Key, kv => kv.Value.WhenUtc);

    public async Task<RefreshRunProgress?> ProgressAsync(long runId, CancellationToken ct)
    {
        var run = await db.Set<ModuleRefreshRun>().AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct);
        if (run is null) return null;
        var counts = await queue.CountsForRunAsync(runId, ct);
        return new RefreshRunProgress(run.RunId, run.RequestId, run.StartedUtc, run.FinishedUtc, (RefreshRunPhase)run.Phase, run.EligibleAtStart,
            counts.GetValueOrDefault(FetchWorkStatus.Queued), counts.GetValueOrDefault(FetchWorkStatus.Claimed), counts.GetValueOrDefault(FetchWorkStatus.Succeeded),
            counts.GetValueOrDefault(FetchWorkStatus.Failed) + counts.GetValueOrDefault(FetchWorkStatus.Cancelled), run.LastAgentHeartbeatUtc);
    }

    /// <summary>The request's newest run, finished or not.</summary>
    public Task<long?> LatestRunIdAsync(long requestId, CancellationToken ct) =>
        db.Set<ModuleRefreshRun>().AsNoTracking().Where(r => r.RequestId == requestId).OrderByDescending(r => r.RunId).Select(r => (long?)r.RunId).FirstOrDefaultAsync(ct);

    /// <summary>Marks the run finished when nothing of it is queued or in progress. True only for the call that finished it (a conditional update), so the end-of-run work
    /// (the final publication, the debounced summaries) runs once.</summary>
    public async Task<bool> FinishIfDoneAsync(long runId, CancellationToken ct)
    {
        var counts = await queue.CountsForRunAsync(runId, ct);
        if (counts.GetValueOrDefault(FetchWorkStatus.Queued) + counts.GetValueOrDefault(FetchWorkStatus.Claimed) > 0) return false;
        var now = Now;
        return await db.Set<ModuleRefreshRun>().Where(r => r.RunId == runId && r.FinishedUtc == null)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.FinishedUtc, now).SetProperty(r => r.Phase, (int)RefreshRunPhase.Done), ct) == 1;
    }

    /// <summary>Unfinished runs, for the worker that closes them.</summary>
    public async Task<IReadOnlyList<(long RunId, long RequestId)>> OpenRunsAsync(CancellationToken ct) =>
        (await db.Set<ModuleRefreshRun>().AsNoTracking().Where(r => r.FinishedUtc == null).Select(r => new { r.RunId, r.RequestId }).ToListAsync(ct))
            .Select(r => (r.RunId, r.RequestId)).ToList();
}
