namespace MCAROC_Analysis.Data.Entities;

/// <summary>What every downstream step of one request must reach before ingestion/analysis can start, and
/// what the enrichment tracks feed once it has. See docs/pipeline-automation-plan.md §3.3 for the full
/// dependency graph.</summary>
public enum PipelineStage
{
    Resolve,
    Unlock,
    Refresh,
    Fetch,
    Ingest,
    Analysis,
    CalcAssurance,
    Dossier,
    Filings,
    Litigation,
    LitigationAnalysis
}

/// <summary>One stage's state at the last reconcile tick. <see cref="Succeeded"/>/<see
/// cref="SucceededWithWarnings"/>/<see cref="Skipped"/>/<see cref="Cancelled"/> are terminal; every other
/// value is revisited on the next tick. <see cref="Waiting"/> is distinct from <see cref="NotStarted"/>: a
/// stage that has never been evaluated is <see cref="NotStarted"/>, one whose prerequisite is known to be
/// unmet (e.g. an unresolved identity) is <see cref="Waiting"/>.</summary>
public enum PipelineStageStateKind
{
    NotStarted,
    Waiting,
    Running,
    Succeeded,
    SucceededWithWarnings,
    RetryScheduled,
    NeedsAttention,
    Skipped,
    Cancelled
}

/// <summary>Distinguishes a <see cref="PipelineStageStateKind.Skipped"/> stage that carries no problem
/// (policy off, a manual-upload request with no Fetch/Refresh to run) from one that does (an integration not
/// configured while its policy demands it, a reused litigation search whose source could not be found) — see
/// docs/pipeline-automation-plan.md §3.4's outcome-precedence table, which reads this to decide between
/// <see cref="PipelineOutcome.Complete"/> and <see cref="PipelineOutcome.CompleteWithWarnings"/>.</summary>
public enum PipelineStageSkipKind
{
    Neutral,
    Warning
}

/// <summary>The request-level rollup, computed by <c>PipelineDecider.Aggregate</c> from every stage's state —
/// first-match precedence, see docs/pipeline-automation-plan.md §3.4.</summary>
public enum PipelineOutcome
{
    InProgress,
    CoreReady,
    Complete,
    CompleteWithWarnings,
    NeedsAttention,
    Cancelled
}

/// <summary>What created this run — reserves <see cref="Api"/>/<see cref="Schedule"/> for the not-yet-built
/// intake stage (docs/pipeline-automation-plan.md's deferred stage 5) so <see cref="PipelineAdopter"/>'s
/// contract doesn't need to change when that ships.</summary>
public enum PipelineRunTrigger
{
    AutoFetch,
    ManualUpload,
    Adopted,
    Api,
    Schedule
}

/// <summary>One request's pipeline run: the read-and-decide loop's own state, kept separate from every
/// domain table it reads (<see cref="Entities.AutoFetchJob"/>, <see cref="IngestionRun"/>, <see
/// cref="AnalysisRun"/>, …) so a bug in the coordinator can never corrupt the data those tables already
/// protect. Exactly one <em>live</em> run per request (see the filtered unique index on <see
/// cref="AppDbContext"/>) — a cancelled/superseded run is never revived, a fresh one is created instead.</summary>
public sealed class PipelineRun
{
    public long PipelineRunId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public PipelineRunTrigger Trigger { get; set; }

    /// <summary>Snapshot of which optional stages/policies were enabled *at creation* (e.g.
    /// AutoLitigationSearch, AutoLitigationAnalysis) — a later config change must never silently alter a run
    /// already in flight; see docs/pipeline-automation-plan.md §3.2 (<c>PipelineRun.PolicyJson</c>).</summary>
    public string PolicyJson { get; set; } = "{}";

    public PipelineOutcome Outcome { get; set; } = PipelineOutcome.InProgress;

    // Deliberately no non-deterministic default (e.g. Guid.NewGuid()) here: EF's model differ bakes a C#
    // property initializer's value into the migration snapshot as a literal, and a fresh value on every
    // process start makes every subsequent `migrations add`/`database update` see a permanent false
    // "pending changes" diff (confirmed via dotnet/efcore#35285 while building this migration). The
    // creating service (PipelineAdopter) always sets this explicitly, propagating the same correlation id
    // already used elsewhere for the request (CorrelationContext.GetOrCreate), so a fallback default isn't
    // needed — required-ness is enforced at the database level instead (IsRequired() in AppDbContext).
    public string CorrelationId { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }
    /// <summary>Set the first time every core stage (Resolve→Dossier) reaches a terminal success and never
    /// cleared afterward — an enrichment stage later needing a human does not withdraw an already-valid
    /// dossier. See docs/pipeline-automation-plan.md §3.4.</summary>
    public DateTime? CoreReadyUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    // Fenced reconciler lease — same pattern as LitigationAiAnalysisRun/CalculationAiAuditRun: a worker whose
    // lease has been reclaimed by recovery can never overwrite state a newer claim has since written.
    public string? ReconcileLeaseOwner { get; set; }
    public Guid? ReconcileLeaseToken { get; set; }
    public DateTime? ReconcileLeaseExpiresUtc { get; set; }

    public byte[]? RowVersion { get; set; }

    public List<PipelineStageState> StageStates { get; set; } = [];
}

/// <summary>One stage's current state within one run. Primary key is (<see cref="PipelineRunId"/>, <see
/// cref="Stage"/>) — every stage has at most one row per run, upserted in place as the reconciler ticks (the
/// history of *why* it changed lives in <see cref="PipelineEvent"/>, not here).</summary>
public sealed class PipelineStageState
{
    public long PipelineRunId { get; set; }
    public PipelineRun? Run { get; set; }
    public PipelineStage Stage { get; set; }

    public PipelineStageStateKind State { get; set; } = PipelineStageStateKind.NotStarted;
    public PipelineStageSkipKind? SkipKind { get; set; }

    /// <summary>Id of the row in the owning domain table that proves this state (an <see
    /// cref="Entities.IngestionRun"/>, <see cref="AnalysisRun"/>, <see cref="McaFilingBatch"/> id, …) — kept
    /// so a human looking at the board can jump straight to the evidence, not just a label.</summary>
    public long? SourceRef { get; set; }

    public int Attempts { get; set; }
    public DateTime? NextAttemptUtc { get; set; }

    /// <summary>Stable reason code (see docs/pipeline-automation-plan.md §6.1's list, e.g.
    /// <c>IDENTITY_AMBIGUOUS</c>, <c>TOOL_UNAVAILABLE</c>) — never a free-text-only reason, so the board and
    /// tests can key off it.</summary>
    public string? ReasonCode { get; set; }
    public string? ReasonDetail { get; set; }

    public DateTime? StartedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public DateTime? LastHeartbeatUtc { get; set; }
}

/// <summary>Append-only record of every coordinator decision — the "why did it do that" audit trail,
/// distinct from (and never a substitute for) the existing human-action audit log. Never updated or deleted
/// once written.</summary>
public sealed class PipelineEvent
{
    public long PipelineEventId { get; set; }
    public long PipelineRunId { get; set; }

    public PipelineStage Stage { get; set; }
    /// <summary>What the coordinator did, e.g. "Started", "Retried", "RaisedAttention", "Skipped" — free text
    /// on purpose (unlike <see cref="PipelineStageState.ReasonCode"/>, this is a log line, not a key other
    /// code branches on).</summary>
    public string Action { get; set; } = string.Empty;
    /// <summary>"system" for an automatic decision, or the reviewer's identity for a manual board action.</summary>
    public string Actor { get; set; } = "system";
    public string? ReasonCode { get; set; }
    public string? CorrelationId { get; set; }
    public DateTime AtUtc { get; set; }
}
