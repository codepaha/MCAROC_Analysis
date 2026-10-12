namespace MCAROC_Analysis.Services.LitigationModule.Freshness;

public enum RefreshPhase { RefreshingCases, FindingMissingCases, FetchingOrders, ReanalysingChangedCases }

public enum RefreshBannerState
{
    /// <summary>No court update is running and none ran: no banner.</summary>
    Idle,
    Running,
    /// <summary>No agent has reported for the heartbeat window although work remains: the banner says so instead of freezing at a percentage.</summary>
    Paused,
    /// <summary>The stage failed; everything keeps showing the last published data.</summary>
    Failed,
    Completed
}

/// <param name="StartedUtc">When the run started; null if none.</param>
/// <param name="EligibleAtStart">Cases that were due for a status fetch when the run started. Disposed and not-due cases are NOT in it, so the bar means something.</param>
/// <param name="Handled">Of those, how many reached an outcome (verified, could not verify, failed after retries).</param>
/// <param name="CompletionTimesUtc">When each handled item finished; the estimate uses the last window of them.</param>
/// <param name="LastAgentHeartbeatUtc">When any agent last reported progress on this request.</param>
/// <param name="StageFailedReason">Set when the live stage failed.</param>
public sealed record RefreshProgressInput(
    DateTime? StartedUtc, int EligibleAtStart, int Handled, IReadOnlyList<DateTime> CompletionTimesUtc, DateTime? LastAgentHeartbeatUtc,
    RefreshPhase Phase, string? StageFailedReason, bool Finished);

public sealed record RefreshBanner(
    RefreshBannerState State, int Percent, int Handled, int Total, TimeSpan? EstimatedRemaining, RefreshPhase Phase, string? Message);

/// <summary>The numbers behind the "Court update in progress" banner (plan section 4.8). Pure: the clock is passed in.</summary>
public static class RefreshBannerCalculator
{
    public static readonly TimeSpan HeartbeatWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan EstimateWindow = TimeSpan.FromMinutes(10);

    public static RefreshBanner Compute(RefreshProgressInput p, DateTime nowUtc)
    {
        if (p.StartedUtc is null) return new(RefreshBannerState.Idle, 0, 0, 0, null, p.Phase, null);
        var total = Math.Max(0, p.EligibleAtStart);
        var handled = Math.Clamp(p.Handled, 0, total);
        var percent = total == 0 ? 100 : (int)Math.Floor(handled * 100.0 / total);

        if (p.StageFailedReason is { Length: > 0 } reason)
            return new(RefreshBannerState.Failed, percent, handled, total, null, p.Phase, $"The court update stopped: {reason}. The last published data is shown.");
        if (p.Finished || handled >= total)
            return new(RefreshBannerState.Completed, 100, total, total, TimeSpan.Zero, p.Phase, null);

        var heartbeat = p.LastAgentHeartbeatUtc ?? p.StartedUtc.Value;
        if (nowUtc - heartbeat > HeartbeatWindow)
            return new(RefreshBannerState.Paused, percent, handled, total, null, p.Phase, "Paused - no fetch agent is working on this.");

        return new(RefreshBannerState.Running, percent, handled, total, Estimate(p, total - handled, nowUtc), p.Phase, null);
    }

    /// <summary>Time left from the speed over the last 10 minutes. Null when there is no recent speed to go on (no guess is shown).</summary>
    private static TimeSpan? Estimate(RefreshProgressInput p, int remaining, DateTime nowUtc)
    {
        var recent = p.CompletionTimesUtc.Count(t => t > nowUtc - EstimateWindow && t <= nowUtc);
        if (recent == 0) return null;
        var perMinute = recent / EstimateWindow.TotalMinutes;
        return TimeSpan.FromMinutes(Math.Ceiling(remaining / perMinute));
    }
}
