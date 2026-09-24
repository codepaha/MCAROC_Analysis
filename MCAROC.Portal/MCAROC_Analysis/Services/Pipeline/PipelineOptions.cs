namespace MCAROC_Analysis.Services.Pipeline;

/// <summary><c>Pipeline</c> configuration section (docs/pipeline-automation-plan.md §4.0). Read through
/// <c>IOptionsMonitor</c> at admit time, so a changed cap takes effect on the next admission without a
/// restart.</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Time zone whose calendar day is the daily-cap bucket (<c>DayKey</c>) — never server-local
    /// time or UTC.</summary>
    public string CapTimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>A <c>Reserved</c> admission with no job/run linked to it for this long is treated as a crash
    /// between admission and the call, and released (after first checking the request for a job/run the
    /// admission may have produced — see <see cref="PaidCallAdmissionService"/>).</summary>
    public int ReservationTtlMinutes { get; set; } = 30;

    /// <summary>How often <see cref="PaidCallAdmissionSweepWorker"/> resolves outstanding reservations.</summary>
    public int AdmissionSweepMinutes { get; set; } = 2;

    public PipelineCapOptions Caps { get; set; } = new();

    /// <summary>Master switch for the coordinator (adoption + reconcile). Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>Observe</c> (the default) records stage states and never starts anything. <c>Enforce</c> lets
    /// the coordinator take the actions its <see cref="Enforce"/> families allow. Switching back to <c>Observe</c>
    /// is the kill switch: it takes effect on the next tick without a restart and leaves all state intact.</summary>
    public PipelineMode Mode { get; set; } = PipelineMode.Observe;

    /// <summary>Which action families <c>Enforce</c> mode may perform (plan §3.6) — each off unless set.</summary>
    public PipelineEnforceOptions Enforce { get; set; } = new();

    /// <summary>After an automatic start is refused (daily cap, the company was searched recently by another
    /// request, not eligible, a transient error) the coordinator waits this long before trying again.</summary>
    public int AutoStartRetryMinutes { get; set; } = 60;

    /// <summary>Requests created on or after this instant are adopted by the periodic sweep. Null (the
    /// default) disables the sweep entirely, so switching the coordinator on never back-fills legacy
    /// requests (e.g. the COASTAL demo dataset) unless an operator deliberately picks a cutoff.</summary>
    public DateTime? AdoptAfterUtc { get; set; }

    public int TickSeconds { get; set; } = 30;
    public int MaxRunsPerTick { get; set; } = 20;

    /// <summary>Which optional enrichment stages a run expects; snapshotted into <c>PipelineRun.PolicyJson</c>
    /// at creation so a later config change doesn't silently alter a run already in flight.</summary>
    public PipelinePolicy Policy { get; set; } = new();

    /// <summary>Unattended unlock (#266): off, so every paid unlock needs a human approval. When on, a locked
    /// company with no approval is unlocked through the same admission path with <c>Trigger=Auto</c>, still
    /// bounded by <c>Caps:UnlockPerDay</c> (which also defaults to 0).</summary>
    public AutoUnlockOptions AutoUnlock { get; set; } = new();

    public bool EnforcesLitigationSearch() => Enabled && Mode == PipelineMode.Enforce && Enforce.Litigation;
}

public enum PipelineMode
{
    Observe,
    Enforce
}

public sealed class PipelineEnforceOptions
{
    /// <summary>Start the litigation search automatically once ingestion is done (and the run's policy wants
    /// it), through the same admission path as the reviewer's button.</summary>
    public bool Litigation { get; set; }
}

public sealed class AutoUnlockOptions
{
    public bool Enabled { get; set; }
}

/// <summary>Enrichment-stage policy. Both default off in code; appsettings.json turns litigation search on (owner,
/// 2026-09-24) and leaves litigation AI analysis off. A stage whose policy is off and which nobody started by hand
/// is reported as a neutral skip, not as missing work.</summary>
public sealed class PipelinePolicy
{
    public bool LitigationSearch { get; set; }
    public bool LitigationAnalysis { get; set; }
}

/// <summary>Daily caps on <c>Auto</c> admissions per kind. Manual admissions are counted against the same
/// counter but never blocked by it. The two real-spend caps default to 0 — inert until an operator sets a
/// positive number, this repo's fail-safe convention.</summary>
public sealed class PipelineCapOptions
{
    /// <summary>Rate-limit guard against BPR's unconfirmed limits, not a spend guard — the search itself is free.</summary>
    public int LitigationSearchPerDay { get; set; } = 50;
    public int LitigationAnalysisPerDay { get; set; } = 0;
    public int UnlockPerDay { get; set; } = 0;
}
