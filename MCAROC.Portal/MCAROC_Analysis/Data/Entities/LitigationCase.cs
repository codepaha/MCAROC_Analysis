using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>One de-duplicated litigation case for a request, persisted from a completed
/// <see cref="LitigationSearchJob"/>'s raw report. Every BPR-returned case is a confirmed business result —
/// this entity carries no Confirmed/Probable/Candidate label or match-decision workflow (see epic #239's
/// confirmed product decisions). De-duplication is conservative and CNR-first: two observations become one
/// row only when <c>Services.LitigationData.LitigationCaseIdentity.CanAutoDedupe</c> says so (a shared,
/// valid CNR plus a compatible proceeding type) — never by fuzzy-matching court/parties/dates. Enforced at
/// the database level too, not just in application code: a unique index on (RequestId, Cnr, ProceedingType)
/// (SQL Server treats each NULL Cnr as distinct, so CNR-less cases never collide) closes the race where two
/// concurrent imports could otherwise both pass the in-memory dedupe check before either commits.
///
/// <see cref="CspId"/>/<see cref="ProviderCaseId"/> here are the *current* (most-recently-observed) provider
/// identity — this row is mutable and gets overwritten on every rerun that re-finds this case. The identity
/// as it stood at each individual observation is preserved separately on
/// <see cref="LitigationCaseSourceReport"/>, since a canonical row alone cannot answer "what did the source
/// actually say the last time BPR was asked."</summary>
public sealed class LitigationCase
{
    public long LitigationCaseId { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }

    public string? ProviderCaseId { get; set; }
    public string? CspId { get; set; }

    /// <summary>Normalized (16-char, uppercase) CNR — see <c>LitigationCaseIdentity.NormaliseCnr</c>. The
    /// only automatic cross-search identity signal; null when the source provided none or an invalid one.</summary>
    public string? Cnr { get; set; }

    /// <summary>Normalized proceeding type — see <c>LitigationCaseIdentity.NormaliseProceedingType</c>. Paired
    /// with <see cref="Cnr"/> as the full de-dup key: a shared CNR with an incompatible proceeding type never
    /// auto-merges.</summary>
    public string? ProceedingType { get; set; }

    // Raw fields exactly as the vendor returned them (see BprLitigationCase) — never transformed, only
    // carried forward from the parser, same "source-faithful" discipline the parser itself follows.
    public string? CourtCategory { get; set; }
    public string? Direction { get; set; }
    public string? CaseClassification { get; set; }
    public string? Type { get; set; }
    public string? Court { get; set; }
    public string? Bench { get; set; }
    public string? CaseNumber { get; set; }
    public string? CaseType { get; set; }
    public string? CaseYear { get; set; }
    public string? CaseStage { get; set; }
    public string? CaseStatus { get; set; }
    public string? Act { get; set; }
    public string? FilingDate { get; set; }
    public string? LastHearingDate { get; set; }
    public string? NextHearingDate { get; set; }
    public string? DecisionDate { get; set; }
    public string? State { get; set; }
    public string? District { get; set; }
    public string? PetitionersJson { get; set; }
    public string? RespondentsJson { get; set; }
    public string? PetitionerAdvocatesJson { get; set; }
    public string? RespondentAdvocatesJson { get; set; }

    /// <summary>When this case was first persisted (from whichever search job found it first).</summary>
    public DateTime FirstSeenUtc { get; set; }

    /// <summary>Updated every time a later search re-surfaces this same case (via CNR-first de-dup) — the
    /// mutable fields above (status, hearing dates, etc.) are refreshed to that later observation, since a
    /// case's real-world state can change between search runs.</summary>
    public DateTime LastSeenUtc { get; set; }

    public List<LitigationCaseOrder> Orders { get; set; } = [];
    public List<LitigationCaseSourceReport> SourceReports { get; set; } = [];
}

/// <summary>One order/judgment record for a <see cref="LitigationCase"/>, exactly as the vendor returned it
/// (see BprLitigationOrder). <see cref="PdfUrl"/> is retained here for #243 (all-orders retrieval) to consume
/// — never published in a client-facing report (see #247's "no source vendor order URL" requirement). A
/// unique index on (LitigationCaseId, PdfUrl, OrderDate, OrderType) backstops the in-memory dedupe check
/// against a concurrent-import race — same reasoning as <see cref="LitigationCase"/>'s own unique index.</summary>
public sealed class LitigationCaseOrder
{
    public long LitigationCaseOrderId { get; set; }
    public long LitigationCaseId { get; set; }
    public LitigationCase? Case { get; set; }

    public string? PdfUrl { get; set; }
    public string? OrderDate { get; set; }
    public string? OrderType { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>Stages of importing one <see cref="LitigationReportSnapshot"/>'s cases. <see cref="Completed"/>
/// and <see cref="Failed"/> are terminal; a snapshot stuck in <see cref="InProgress"/> past its
/// <c>LeaseExpiresUtc</c> (a crashed worker) is reclaimable again.</summary>
public enum LitigationReportSnapshotStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

/// <summary>An immutable copy of exactly one completed BPR report, keyed by (job id, report hash) — never
/// mutated after creation except for its own import-progress bookkeeping below. This exists because a
/// request's <see cref="LitigationSearchJob"/> row is reused in place on every rerun
/// (<c>CreateOrResetJobAsync</c>): the job's own <c>RawReportBytes</c>/<c>RawResponseHash</c> get overwritten
/// by the next rerun, so without a separate immutable copy, nothing could reconstruct an earlier report (or
/// the provider/CSP identity it asserted — see <see cref="LitigationCaseSourceReport"/>) once a later rerun
/// replaced it.
///
/// Also the crash-safe unit of import work: <see cref="Status"/>/<see cref="LeaseExpiresUtc"/> make claiming
/// one snapshot for processing atomic and reclaimable after a crash (mirrors <c>LitigationSearchJob</c>'s own
/// lease reasoning), and <see cref="CasesPersistedCount"/> lets a resumed import skip cases a prior, crashed
/// attempt already committed — positional, not identity-based, so it correctly resumes even for a case with
/// no CNR (which can never be recognized as "the same case" by identity alone). Concurrency-token-protected
/// (<see cref="RowVersion"/>): every mutation this type undergoes — the initial claim and every per-case
/// progress update — goes through EF's normal optimistic-concurrency check, so two workers racing to process
/// the same snapshot can never both believe they own it; the loser's SaveChanges throws
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> and stops immediately rather than
/// duplicating work the winner already committed.</summary>
public sealed class LitigationReportSnapshot
{
    public long LitigationReportSnapshotId { get; set; }
    public long LitigationSearchJobId { get; set; }
    public LitigationSearchJob? SearchJob { get; set; }

    /// <summary>Copied from <c>LitigationSearchJob.RawResponseHash</c> at snapshot-creation time — see this
    /// type's remarks for why the job's own copy cannot be relied on after a rerun.</summary>
    public string ReportHash { get; set; } = string.Empty;

    public BprReportFormat ReportFormat { get; set; }

    /// <summary>The complete raw report payload, copied at snapshot-creation time — immutable from then on,
    /// unlike the same bytes on the job row.</summary>
    public byte[] RawReportBytes { get; set; } = [];
    public long RawReportByteLength { get; set; }

    /// <summary>When BPR actually returned this report (copied from the job's CompletedUtc at the moment this
    /// snapshot was created) — retrieval metadata, distinct from <see cref="CreatedUtc"/> below.</summary>
    public DateTime RetrievedUtc { get; set; }

    public LitigationReportSnapshotStatus Status { get; set; } = LitigationReportSnapshotStatus.Pending;
    public int AttemptCount { get; set; }

    /// <summary>How many of this report's parsed cases (in parser order) have been durably committed —
    /// case/orders/source-report link, all in the same transaction as this counter's own increment. A resumed
    /// import starts at this index, never reprocessing earlier ones. See this type's remarks for why this is
    /// positional rather than identity-based.</summary>
    public int CasesPersistedCount { get; set; }

    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }

    /// <summary>EF-managed SQL Server <c>rowversion</c> — see this type's remarks. Never read or compared by
    /// application code directly; EF includes it in every UPDATE's WHERE clause automatically.</summary>
    public byte[]? RowVersion { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public bool IsTerminal => Status is LitigationReportSnapshotStatus.Completed or LitigationReportSnapshotStatus.Failed;
}

/// <summary>Links a <see cref="LitigationCase"/> to every <see cref="LitigationReportSnapshot"/> that
/// surfaced it — the auditable provenance trail: "all source keywords remain auditable" (join through the
/// snapshot to the job's KeywordsJson) and "retain merged-source provenance" (more than one link means more
/// than one search run found this same real-world case).
///
/// <see cref="ProviderCaseId"/>/<see cref="CspId"/> here are what *this specific report* asserted at the time
/// it was retrieved — captured per-observation because <see cref="LitigationCase"/>'s own copy is mutable and
/// gets overwritten by the next rerun; without this, the provider identity a since-superseded report actually
/// reported could never be reconstructed once a later rerun replaced it on the canonical row.</summary>
public sealed class LitigationCaseSourceReport
{
    public long LitigationCaseSourceReportId { get; set; }
    public long LitigationCaseId { get; set; }
    public LitigationCase? Case { get; set; }
    public long LitigationReportSnapshotId { get; set; }
    public LitigationReportSnapshot? ReportSnapshot { get; set; }

    public string? ProviderCaseId { get; set; }
    public string? CspId { get; set; }

    public DateTime FirstSeenUtc { get; set; }
}
