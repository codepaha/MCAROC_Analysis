namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Pure decision policy for an internal company refresh. Persistence and provider I/O deliberately
/// live elsewhere: callers first use this policy to decide whether they may reuse a verified snapshot,
/// need to start a refresh, or must join the single refresh already in progress.</summary>
public static class CompanyRefreshPolicy
{
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromHours(24);

    public static CompanyRefreshDecision Decide(CompanyRefreshState state, DateTime utcNow)
    {
        if (!state.IsUnlocked)
            return CompanyRefreshDecision.Locked;

        if (state.HasActiveRefresh)
            return CompanyRefreshDecision.JoinActiveRefresh;

        // A future timestamp can arise from clock skew; it is not stale merely because this host's
        // clock is behind the host that recorded the successful snapshot.
        if (state.LastSuccessfulSnapshotUtc is { } snapshotUtc
            && utcNow - snapshotUtc <= FreshnessWindow)
            return CompanyRefreshDecision.ReuseFreshSnapshot;

        return CompanyRefreshDecision.StartRefresh;
    }
}

public sealed record CompanyRefreshState(
    bool IsUnlocked,
    DateTime? LastSuccessfulSnapshotUtc,
    bool HasActiveRefresh);

public enum CompanyRefreshDecision
{
    Locked,
    ReuseFreshSnapshot,
    JoinActiveRefresh,
    StartRefresh
}
