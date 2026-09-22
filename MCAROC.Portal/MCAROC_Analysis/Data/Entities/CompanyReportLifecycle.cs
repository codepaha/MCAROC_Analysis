namespace MCAROC_Analysis.Data.Entities;

/// <summary>Where one company's reference-tool report is in the unlock/refresh spec's own state machine
/// (docs/pipeline-automation-plan.md §5.7, sourced from the captured MCA-ROC user-flow spec). Feeds <see
/// cref="Services.AutoFetch.CompanyRefreshState"/> so <see
/// cref="Services.AutoFetch.CompanyRefreshPolicy.Decide"/> — already written, never wired to anything — is
/// used exactly as it was authored.</summary>
public enum CompanyReportLifecycleState
{
    NeverRequested,
    Locked,
    Unlocking,
    Unlocked,
    Refreshing,
    RefreshPending,
    RefreshFailed,
    Expired
}

/// <summary>One row per canonical company identifier (CIN/LLPIN), <em>not</em> per request — the unlocked
/// report and its refresh cadence are shared underneath across every request for that company, per the
/// captured spec. <see cref="Identifier"/> is trimmed/upper-cased the same way <see
/// cref="McaRequest.AutoFetchCompanyIdentifier"/> already is.</summary>
public sealed class CompanyReportLifecycle
{
    public long CompanyReportLifecycleId { get; set; }
    public string Identifier { get; set; } = string.Empty;

    public CompanyReportLifecycleState State { get; set; } = CompanyReportLifecycleState.NeverRequested;

    /// <summary>The original unlock timestamp — immutable once set until a fresh unlock after <see
    /// cref="CompanyReportLifecycleState.Expired"/>. A refresh never touches this (confirmed live,
    /// docs/reference-tool-refresh-unlock-contract.md §3.3): the 12-month window
    /// (<c>UnlockedUtc + 1 year - 1 day</c>) is computed from it, strictly.</summary>
    public DateTime? UnlockedUtc { get; set; }

    public DateTime? LastRefreshRequestedUtc { get; set; }
    public DateTime? LastRefreshCompletedUtc { get; set; }

    /// <summary>Non-null exactly while one refresh is in flight — the one-winner claim target: a request
    /// finding this already set joins it instead of triggering another (the spec's 24h/join-pending rule).</summary>
    public long? ActiveRefreshId { get; set; }
    /// <summary>Requested-time + 36h (the spec's own outer wait bound) — past this, the refresh is timed out
    /// (<c>REFRESH_TIMEOUT</c>) rather than polled forever.</summary>
    public DateTime? RefreshDeadlineUtc { get; set; }

    public byte[]? RowVersion { get; set; }
}
