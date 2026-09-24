using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCAROC_Analysis.Services;

namespace MCAROC_Analysis.Services.AutoFetch;

public enum RefreshGateKind
{
    /// <summary>Unlocked, and the tool's data is under 24 hours old — safe to export now.</summary>
    Ready,
    /// <summary>A refresh is pending at the tool (this call started it or joined it) — park and poll.</summary>
    Waiting,
    /// <summary>Not unlocked (or the 12-month unlock expired) — nothing may be exported.</summary>
    Locked,
    /// <summary>The refresh outlived its deadline — needs a human; a retry starts a new one.</summary>
    TimedOut,
    /// <summary>The MCA portal is in maintenance — transient, try again later.</summary>
    Deferred
}

public sealed record RefreshGateResult(RefreshGateKind Kind, string Message, DateTime? DataAsOfUtc = null);

/// <summary>The company-level unlock/refresh lifecycle (issue #229, docs/pipeline-automation-plan.md §5.7):
/// decides whether a company's data may be exported, and drives the tool's refresh until it may. One
/// <see cref="CompanyReportLifecycle"/> row per company, shared by every request for it.
///
/// Every write is a conditional single-statement UPDATE, so concurrent callers (several jobs for one company,
/// the poll worker, several instances) agree without locks:
/// <list type="bullet">
/// <item>Starting a refresh is a one-winner claim on <c>ActiveRefreshId IS NULL</c> and a stale last refresh;
/// everyone else joins the pending one instead of triggering another.</item>
/// <item>A refresh completes only when the tool reports nothing pending <em>and</em> its data is at least as new
/// as our request. If it reports nothing pending with older data (the request never reached the tool — e.g. a
/// crash between claim and call), the request is re-sent (free) until the deadline, never treated as done.</item>
/// <item>Completion and timeout are fenced on the claim id, so a slow poller can't close a newer refresh.</item>
/// </list></summary>
public sealed class CompanyRefreshService(
    AppDbContext db, ReferenceToolClient client, IOptions<ReferenceToolOptions> options, TimeProvider time, ILogger<CompanyRefreshService> logger)
{
    /// <summary>The tool keeps an unlock valid until <c>addedAt + 1 year - 1 day</c>.</summary>
    public static DateTime UnlockValidTill(DateTime unlockedUtc) => unlockedUtc.AddYears(1).AddDays(-1);

    private TimeSpan RefreshTimeout => TimeSpan.FromHours(Math.Max(1, options.Value.RefreshTimeoutHours));

    public async Task<RefreshGateResult> EvaluateAsync(string identifier, string bid, CancellationToken ct)
    {
        identifier = identifier.Trim().ToUpperInvariant();
        await EnsureRowAsync(identifier, ct);
        var now = time.GetUtcNow().UtcDateTime;

        var asset = await client.GetAssetStatusAsync(bid, ct);
        if (asset.AddedAt is null)
        {
            var previous = await Rows(identifier).Select(r => r.UnlockedUtc).SingleAsync(ct);
            var expired = previous is { } unlocked && now > UnlockValidTill(unlocked);
            await Rows(identifier).ExecuteUpdateAsync(s => s.SetProperty(r => r.State,
                expired ? CompanyReportLifecycleState.Expired : CompanyReportLifecycleState.Locked), ct);
            return new RefreshGateResult(RefreshGateKind.Locked, expired
                ? $"The company's 12-month unlock in the reference tool expired on {Ist.Date(UnlockValidTill(previous!.Value))}; it must be unlocked again before its data can be fetched."
                : "The company is not unlocked in the reference tool, so its data can't be fetched. Unlock it there, then retry.");
        }

        // Adopt whatever unlock the tool reports (ours or anyone's). A refresh never moves this date.
        var addedUtc = asset.AddedAt.Value.UtcDateTime;
        await Rows(identifier).Where(r => r.UnlockedUtc == null || r.UnlockedUtc != addedUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.UnlockedUtc, addedUtc), ct);

        var row = await Rows(identifier).AsNoTracking().SingleAsync(ct);
        if (row.ActiveRefreshId is { } activeId)
            return await PollAsync(identifier, bid, activeId, row, now, ct);

        // The tool's own data timestamp counts too: someone else's refresh is as good as ours.
        var dataAsOf = (await client.GetDataAsOfAsync(bid, ct))?.UtcDateTime;
        var lastFresh = Max(row.LastRefreshCompletedUtc, dataAsOf);
        if (lastFresh is { } fresh && fresh > (row.LastRefreshCompletedUtc ?? DateTime.MinValue))
            await Rows(identifier).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastRefreshCompletedUtc, fresh), ct);

        var decision = CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, lastFresh, HasActiveRefresh: false), now);
        if (decision == CompanyRefreshDecision.ReuseFreshSnapshot)
        {
            await Rows(identifier).Where(r => r.ActiveRefreshId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, CompanyReportLifecycleState.Unlocked), ct);
            return new RefreshGateResult(RefreshGateKind.Ready, $"The reference tool's data is current (as of {Ist.Format(lastFresh)}).", lastFresh);
        }

        if (!await client.IsMcaAvailableForRefreshAsync(ct))
            return new RefreshGateResult(RefreshGateKind.Deferred, "The MCA portal is under maintenance; the refresh will be requested once it is back.");

        var claimId = NewClaimId();
        var staleBefore = now - CompanyRefreshPolicy.FreshnessWindow;
        var claimed = await Rows(identifier)
            .Where(r => r.ActiveRefreshId == null && (r.LastRefreshCompletedUtc == null || r.LastRefreshCompletedUtc < staleBefore))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ActiveRefreshId, claimId)
                .SetProperty(r => r.State, CompanyReportLifecycleState.Refreshing)
                .SetProperty(r => r.LastRefreshRequestedUtc, now)
                .SetProperty(r => r.RefreshDeadlineUtc, now.Add(RefreshTimeout)), ct);
        if (claimed == 0)
            return new RefreshGateResult(RefreshGateKind.Waiting, "Waiting for the reference tool to finish refreshing this company's data (already requested).");

        try
        {
            await client.RequestRefreshAsync(bid, ct);
        }
        catch
        {
            // Nothing reached the tool (or we can't know it did and it's free to repeat) — give the claim back.
            await Rows(identifier).Where(r => r.ActiveRefreshId == claimId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ActiveRefreshId, (long?)null)
                .SetProperty(r => r.State, CompanyReportLifecycleState.Unlocked)
                .SetProperty(r => r.RefreshDeadlineUtc, (DateTime?)null), CancellationToken.None);
            throw;
        }

        logger.LogInformation("Requested a reference-tool refresh for {Identifier} (data as of {DataAsOf})", identifier, lastFresh);
        return new RefreshGateResult(RefreshGateKind.Waiting,
            $"Waiting for the reference tool to refresh this company's data (requested {Ist.Format(now)}; times out {Ist.Format(now.Add(RefreshTimeout))}).");
    }

    private async Task<RefreshGateResult> PollAsync(string identifier, string bid, long activeId, CompanyReportLifecycle row, DateTime now, CancellationToken ct)
    {
        var requestedUtc = row.LastRefreshRequestedUtc ?? now;
        var deadline = row.RefreshDeadlineUtc ?? requestedUtc.Add(RefreshTimeout);

        if (await client.GetRefreshStatusAsync(bid, ct) == ReferenceRefreshStatus.NoPendingRequest)
        {
            var dataAsOf = (await client.GetDataAsOfAsync(bid, ct))?.UtcDateTime;
            if (dataAsOf is { } asOf && asOf >= requestedUtc)
            {
                var completed = await Rows(identifier).Where(r => r.ActiveRefreshId == activeId).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.ActiveRefreshId, (long?)null)
                    .SetProperty(r => r.State, CompanyReportLifecycleState.Unlocked)
                    .SetProperty(r => r.LastRefreshCompletedUtc, asOf)
                    .SetProperty(r => r.RefreshDeadlineUtc, (DateTime?)null), ct);
                if (completed == 1)
                    logger.LogInformation("Reference-tool refresh for {Identifier} completed (data as of {DataAsOf})", identifier, asOf);

                // Newer than the request is not the same as fresh: a refresh that lands more than 24 hours after
                // it was requested can carry data that is already stale. Only fresh data may be exported; stale
                // data is re-evaluated like any stale company, which starts (or joins) a new refresh.
                if (CompanyRefreshPolicy.Decide(new CompanyRefreshState(true, asOf, HasActiveRefresh: false), now) == CompanyRefreshDecision.ReuseFreshSnapshot)
                    return new RefreshGateResult(RefreshGateKind.Ready, $"The reference tool finished refreshing this company's data (as of {Ist.Format(asOf)}).", asOf);
                logger.LogWarning("Reference-tool refresh for {Identifier} landed with data already over 24 hours old ({DataAsOf}); refreshing again", identifier, asOf);
                return await EvaluateAsync(identifier, bid, ct);
            }

            // Nothing pending, yet the data predates our request: the request never took effect. Re-send it
            // (free) rather than accept stale data — bounded by the same deadline.
            if (now <= deadline)
            {
                await client.RequestRefreshAsync(bid, ct);
                return new RefreshGateResult(RefreshGateKind.Waiting,
                    $"Waiting for the reference tool to refresh this company's data (re-requested; times out {Ist.Format(deadline)}).");
            }
        }
        else if (now <= deadline)
        {
            return new RefreshGateResult(RefreshGateKind.Waiting,
                $"Waiting for the reference tool to refresh this company's data (requested {Ist.Format(requestedUtc)}; times out {Ist.Format(deadline)}).");
        }

        await Rows(identifier).Where(r => r.ActiveRefreshId == activeId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.ActiveRefreshId, (long?)null)
            .SetProperty(r => r.State, CompanyReportLifecycleState.RefreshFailed), ct);
        logger.LogWarning("Reference-tool refresh for {Identifier} timed out (requested {Requested})", identifier, requestedUtc);
        return new RefreshGateResult(RefreshGateKind.TimedOut,
            $"REFRESH_TIMEOUT: the reference tool did not finish refreshing this company's data within {RefreshTimeout.TotalHours:0} hours of the request at {Ist.Format(requestedUtc)}. Retry to request a new refresh.");
    }

    private IQueryable<CompanyReportLifecycle> Rows(string identifier) =>
        db.CompanyReportLifecycles.Where(r => r.Identifier == identifier);

    /// <summary>Insert-if-absent; a concurrent insert of the same company loses on the unique index and is
    /// ignored — the row it wanted now exists.</summary>
    private async Task EnsureRowAsync(string identifier, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($@"
IF NOT EXISTS (SELECT 1 FROM CompanyReportLifecycles WHERE Identifier = {identifier})
INSERT INTO CompanyReportLifecycles (Identifier, State) VALUES ({identifier}, {CompanyReportLifecycleState.NeverRequested.ToString()})", ct);
        }
        catch (SqlException ex) when (ex.Number is 2627 or 2601) { }
    }

    private static long NewClaimId() => Math.Abs(BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0) & long.MaxValue) | 1;

    private static DateTime? Max(DateTime? a, DateTime? b) => a is null ? b : b is null ? a : a > b ? a : b;
}
