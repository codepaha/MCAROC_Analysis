namespace MCAROC_Analysis.Data.Entities;

/// <summary>The three calls this pipeline may admit. Only <see cref="ReferenceUnlock"/> and <see
/// cref="LitigationAnalysis"/> spend real money (owner-confirmed, docs/pipeline-automation-plan.md §4.0 /
/// §15 #4); <see cref="LitigationSearch"/> queries BPR's own litigation data lake (no per-call charge) and is
/// admitted through the same path only to dedupe/reuse work and guard against BPR's own unconfirmed
/// rate/concurrency limits — never to protect a budget.</summary>
public enum PaidCallKind
{
    LitigationSearch,
    LitigationAnalysis,
    ReferenceUnlock
}

public enum PaidCallTrigger
{
    Auto,
    Manual
}

/// <summary><see cref="Reserved"/> is provisional (the transaction that took the scope, before the real call
/// is even attempted); <see cref="Committed"/> means the call may have happened (fail closed on an unknown
/// outcome — see docs/pipeline-automation-plan.md §4.0's resolution rules) and the scope's cooldown starts;
/// <see cref="Released"/> means it is provable that nothing was spent, and the counter slot is given back.</summary>
public enum PaidCallAdmissionState
{
    Reserved,
    Committed,
    Released
}

/// <summary>One row per (<see cref="Kind"/>, <see cref="ScopeKey"/>) — the dedup/cooldown unit for that kind
/// (a company for unlock/search, an origin snapshot for analysis). <see cref="ActiveAdmissionId"/> is the
/// one-winner mutex: null when idle, set for the lifetime of exactly one in-flight admission. <see
/// cref="LastCommittedUtc"/> is the freshness clock a later admission's window check reads — set only by a
/// <see cref="PaidCallAdmissionState.Committed"/> resolution, never touched by anything else (in particular,
/// never by a refresh — see docs/pipeline-automation-plan.md §5.7's "a refresh never extends
/// UnlockedUtc/validTill", the same invariant applied here to the admission ledger's own cooldown).</summary>
public sealed class SpendScope
{
    public long SpendScopeId { get; set; }
    public PaidCallKind Kind { get; set; }
    public string ScopeKey { get; set; } = string.Empty;

    public long? ActiveAdmissionId { get; set; }
    public DateTime? LastCommittedUtc { get; set; }

    public byte[]? RowVersion { get; set; }
}

/// <summary>One row per (<see cref="Kind"/>, <see cref="DayKey"/>) — the daily cap counter. <see
/// cref="Used"/> is incremented for both <see cref="PaidCallTrigger.Auto"/> and <see
/// cref="PaidCallTrigger.Manual"/> admissions (owner decision, round 5: manual is never blocked by the cap
/// but always counted), and decremented back on a provable <see cref="PaidCallAdmissionState.Released"/>.
/// <see cref="DayKey"/> is a business day in <c>Pipeline:CapTimeZone</c> (default Asia/Kolkata), never
/// server-local time or UTC — see docs/pipeline-automation-plan.md §4.0.</summary>
public sealed class SpendCounter
{
    public long SpendCounterId { get; set; }
    public PaidCallKind Kind { get; set; }
    public DateOnly DayKey { get; set; }
    public int Used { get; set; }

    public byte[]? RowVersion { get; set; }
}

/// <summary>Append-only ledger — every admission ever granted, auto or manual, and how it resolved. This is
/// the durable spend history the overwrite-in-place job/run rows (<see cref="Entities.LitigationSearchJob"/>,
/// <see cref="LitigationAiAnalysisRun"/>, the reference-tool unlock call) cannot provide on their own, and it
/// is never updated once a resolution is written except for the state transition itself.</summary>
public sealed class PaidCallAdmission
{
    public long PaidCallAdmissionId { get; set; }

    public PaidCallKind Kind { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public DateOnly DayKey { get; set; }
    public PaidCallTrigger Trigger { get; set; }

    public long RequestId { get; set; }
    public long? ClientId { get; set; }

    public PaidCallAdmissionState State { get; set; } = PaidCallAdmissionState.Reserved;
    /// <summary>Id of the job/run row this admission paid for once one exists (a <see
    /// cref="Entities.LitigationSearchJob"/>, <see cref="LitigationAiAnalysisRun"/>, or the reference-tool
    /// unlock's own tracking row) — null while still <see cref="PaidCallAdmissionState.Reserved"/>.</summary>
    public long? ReferenceId { get; set; }

    public DateTime ReservedUtc { get; set; }
    public DateTime? ResolvedUtc { get; set; }

    public string? CorrelationId { get; set; }
}
