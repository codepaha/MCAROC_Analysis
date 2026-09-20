using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>Stages of one BPR litigation search, in order. Everything before <see cref="Completed"/> /
/// <see cref="Failed"/> is non-terminal and is reset to <see cref="Pending"/> by the worker's startup
/// recovery sweep once its lease has expired (mirrors <c>CalculationAiAuditRun</c>'s lease pattern, not
/// <c>AutoFetchJob</c>'s simpler always-reset-on-restart one — a BPR poll can legitimately still be running
/// when the app restarts, so recovery must not requeue a duplicate registration).</summary>
public enum LitigationSearchJobStatus
{
    Pending,
    Authenticating,
    Registering,
    Polling,
    Completed,
    Failed
}

/// <summary>One request's BPR litigation search: authenticate, register the approved keyword set, poll for
/// the report, and retain it (raw, unparsed) for #242 to persist as cases. A request has at most one job,
/// re-run in place — mirrors <c>AutoFetchJob</c>'s one-per-request model. This entity deliberately stops at
/// "retain the raw report": parsing it into <c>LitigationCase</c>/<c>LitigationCaseMatch</c> rows is #242's
/// scope, not this one's.</summary>
public sealed class LitigationSearchJob
{
    public long LitigationSearchJobId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public LitigationSearchJobStatus Status { get; set; } = LitigationSearchJobStatus.Pending;
    public int ProgressPercent { get; set; }
    public string? StatusMessage { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>"entity_type" sent on registration. Defaults to <c>BprLitigationOptions.DefaultEntityType</c>
    /// ("individual") at job-creation time — the vendor's sample collection never demonstrates a
    /// corporate/LLP value; see docs/litigation-data-lake-integration.md.</summary>
    public string EntityType { get; set; } = "individual";
    public string ApplicationCustomerId { get; set; } = string.Empty;

    /// <summary>JSON array of the exact keyword objects submitted (<c>[{"value":"...","source":"..."}]</c>),
    /// built by <see cref="LitigationKeywordPlanner"/> before this job is created — never invented here.
    /// Kept as a JSON column rather than a child table: it is audit data about one registration call, not an
    /// independently queried relation (unlike the future per-case keyword provenance #242 will add).</summary>
    public string KeywordsJson { get; set; } = "[]";

    /// <summary>Set the instant POST bprjob/register confirms success — the orchestrator skips straight to
    /// polling once this is set, so a normal retry never re-registers. This narrows, but does not eliminate,
    /// the crash window: if the process dies after BPR accepts the call but before this column is persisted,
    /// recovery cannot tell whether a vendor-side job now exists. The confirmed BPR contract has no
    /// idempotency key and no way to look up a prior registration by customer id, so a blind retry in that
    /// state risks a duplicate vendor-side search. <see cref="RegistrationAttemptedUtc"/> exists precisely to
    /// catch that state and refuse to auto-retry rather than guess.</summary>
    public string? VendorJobId { get; set; }
    public DateTime? RegisteredUtc { get; set; }

    /// <summary>Set immediately before every POST bprjob/register call, before the call is made — durable
    /// evidence that an attempt was in flight. If a later claim finds this set but <see cref="VendorJobId"/>
    /// still null, the previous attempt's outcome is unknown (BPR may or may not have created a job) and the
    /// orchestrator fails the job closed rather than risk a duplicate registration; an operator must check
    /// BPR directly and either set <see cref="VendorJobId"/> by hand or clear this column before a fresh
    /// attempt is allowed.</summary>
    public DateTime? RegistrationAttemptedUtc { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Durable worker lease — see <see cref="LitigationSearchJobStatus"/>. LeaseOwner
    /// ("machine:pid") is diagnostic only. <see cref="LeaseToken"/> + <see cref="LeaseExpiresUtc"/> are what
    /// enforce correctness: every mutation this job's processing makes is conditioned on
    /// <c>LeaseToken == (the token this attempt was claimed with) AND LeaseExpiresUtc > now</c>, so a worker
    /// whose lease has been reclaimed by someone else (its own lease expired and recovery reassigned the job)
    /// can no longer overwrite state a newer claim has since written — LeaseOwner alone cannot do this since
    /// it is reused across every claim by the same process and carries no per-claim identity.</summary>
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    /// <summary>Set after a failed attempt so recovery/dispatch doesn't re-claim this row until the backoff
    /// window has passed.</summary>
    public DateTime? NextAttemptUtc { get; set; }

    public BprReportFormat ReportFormat { get; set; } = BprReportFormat.Unknown;

    /// <summary>The complete raw report payload exactly as received (JSON text or XLSX bytes), retained so
    /// #242 can parse it and so a disputed case can later be checked against exactly what the vendor
    /// returned. Not a substitute for <see cref="RawResponseHash"/> or vice versa — see the same reasoning
    /// on <c>CalculationAiAuditRun.RawResponseJson</c>.</summary>
    public byte[]? RawReportBytes { get; set; }

    /// <summary>SHA-256 of <see cref="RawReportBytes"/> — a cheap tamper/dedup check, never a substitute for
    /// the retained bytes above.</summary>
    public string? RawResponseHash { get; set; }
    public long? RawReportByteLength { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public bool IsTerminal => Status is LitigationSearchJobStatus.Completed or LitigationSearchJobStatus.Failed;
}
