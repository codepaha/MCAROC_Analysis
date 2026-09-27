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

    /// <summary>Plan §6.2: how many coordinator-driven retries a stage gets (each stage's own internal
    /// retries — a job's download attempts, an analysis run's FailOrRetry — already happened before the
    /// stage ever reaches the coordinator as terminal-Failed; this caps retries of that terminal outcome).
    /// The 4th exhausted attempt becomes <c>NeedsAttention(RETRIES_EXHAUSTED)</c> instead of a 5th try.</summary>
    public int MaxCoordinatorAttempts { get; set; } = 4;

    /// <summary>Which optional enrichment stages a run expects; snapshotted into <c>PipelineRun.PolicyJson</c>
    /// at creation so a later config change doesn't silently alter a run already in flight.</summary>
    public PipelinePolicy Policy { get; set; } = new();

    /// <summary>Unattended unlock (#266): off, so every paid unlock needs a human approval. When on, a locked
    /// company with no approval is unlocked through the same admission path with <c>Trigger=Auto</c>, still
    /// bounded by <c>Caps:UnlockPerDay</c> (which also defaults to 0).</summary>
    public AutoUnlockOptions AutoUnlock { get; set; } = new();

    /// <summary>Plan §6.3: per-stage staleness thresholds for detecting a runtime stall — a worker that
    /// claimed a stage and then died mid-task without its own failure path ever firing, so the stage would
    /// otherwise sit "Running" forever. Always evaluated (not gated by <see cref="Mode"/> or <see cref="Enforce"/>):
    /// stall detection only ever produces <c>NeedsAttention(STAGE_STALLED)</c>, never an action, so it is as
    /// safe in Observe mode as every other decider verdict.</summary>
    public PipelineStallOptions Stall { get; set; } = new();

    /// <summary>Plan §6.5: bounds how many requests the coordinator lets actively run a litigation search or
    /// litigation AI analysis at once — the two stages that call an external, rate/quota-limited dependency
    /// on the coordinator's own initiative (BPR's unconfirmed rate limits per epic #262's own scope note;
    /// Vertex AI's quota for the analysis). 0 (the default) means unlimited — inert until an operator sets a
    /// positive number, this repo's usual fail-safe convention. A request over the cap is deferred exactly
    /// like any other refused auto-start (same <c>AutoStartRetryMinutes</c> backoff), never blocked outright —
    /// a human's manual start is never subject to this cap.</summary>
    public int MaxConcurrentRuns { get; set; }

    public bool EnforcesLitigationSearch() => Enabled && Mode == PipelineMode.Enforce && Enforce.Litigation;
    public bool EnforcesLitigationAnalysis() => Enabled && Mode == PipelineMode.Enforce && Enforce.LitigationAnalysis;
    public bool EnforcesDossierPreRender() => Enabled && Mode == PipelineMode.Enforce && Enforce.Dossier;
    public bool EnforcesRetries() => Enabled && Mode == PipelineMode.Enforce && Enforce.Retries;
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

    /// <summary>Start litigation AI analysis automatically once the search is done and every order document has
    /// finished downloading/chunking (plan §4.2), through the same admission path as the reviewer's button.
    /// Off by default — owner decision 2026-09-24 (plan §16) keeps litigation analysis off with cap 0 even
    /// with the rest of Enforce mode on.</summary>
    public bool LitigationAnalysis { get; set; }

    /// <summary>Pre-render the dossier PDF once <c>CalcAssurance</c> succeeds (plan §4.3), through the same
    /// cache path <c>DossierController.Download</c> uses — purely a latency optimisation for the first
    /// download; a render failure here never blocks the on-demand render the controller still falls back to.</summary>
    public bool Dossier { get; set; }

    /// <summary>Plan §6.2/rollout order (§7: "...→ C3 → D with Enforce:Retries"): let the coordinator retry a
    /// <see cref="PipelineFailureClass.Transient"/> stage failure on its own, with backoff, up to
    /// <see cref="PipelineOptions.MaxCoordinatorAttempts"/>. Off by default — a reviewer's manual Retry stays
    /// the only path until this is turned on.</summary>
    public bool Retries { get; set; }
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

/// <summary>Plan §6.3's per-stage thresholds. Litigation and LitigationAnalysis aren't here — their signal
/// is a fenced lease's own expiry (already a deadline, not a duration to configure).</summary>
public sealed class PipelineStallOptions
{
    /// <summary>Fetch, and — while filing documents download after the main export completes — Filings too;
    /// both read the same <c>AutoFetchJob.HeartbeatUtc</c>. Downloads flush progress often enough that 20
    /// minutes of silence means the worker is gone, not just slow.</summary>
    public int FetchMinutes { get; set; } = 20;

    /// <summary>Analysis, from <c>AnalysisRun.StartedDate</c> — a fixed start time rather than a renewing
    /// heartbeat (the orchestrator has no progress column), so this is simply "running longer than a normal
    /// pass ever takes."</summary>
    public int AnalysisMinutes { get; set; } = 15;
}
