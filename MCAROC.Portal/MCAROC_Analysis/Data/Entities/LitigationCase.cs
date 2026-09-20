namespace MCAROC_Analysis.Data.Entities;

/// <summary>One de-duplicated litigation case for a request, persisted from a completed
/// <see cref="LitigationSearchJob"/>'s raw report. Every BPR-returned case is a confirmed business result —
/// this entity carries no Confirmed/Probable/Candidate label or match-decision workflow (see epic #239's
/// confirmed product decisions). De-duplication is conservative and CNR-first: two observations become one
/// row only when <c>Services.LitigationData.LitigationCaseIdentity.CanAutoDedupe</c> says so (a shared,
/// valid CNR plus a compatible proceeding type) — never by fuzzy-matching court/parties/dates. <see cref="CspId"/>
/// is retained as the BPR/provider case identity but is never used as a merge key.</summary>
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
/// — never published in a client-facing report (see #247's "no source vendor order URL" requirement).</summary>
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

/// <summary>Links a <see cref="LitigationCase"/> to every completed report that surfaced it — the auditable
/// provenance trail: "all source keywords remain auditable" (join through to the job's KeywordsJson) and
/// "retain merged-source provenance" (more than one link means more than one search run found this same
/// real-world case).
///
/// Identified by <see cref="LitigationSearchJobId"/> <b>plus</b> <see cref="ReportHash"/> (copied from
/// <c>LitigationSearchJob.RawResponseHash</c>), not by job id alone: a request has at most one
/// <c>LitigationSearchJob</c> row, reused in place on every rerun (<c>CreateOrResetJobAsync</c>) — so the
/// same job id recurs across genuinely different search runs. Keying idempotency on job id alone would make
/// the *first* rerun's persistence permanently skip every subsequent rerun's cases forever, since the job id
/// never changes. Keying on (job id, report hash) instead means: re-processing the exact same completed
/// report is a no-op (matching hash), but a genuine rerun that produced a new report (different hash, even
/// from the same job row) is processed and, via CNR-first de-dup, correctly recognized as additional
/// provenance for cases it re-finds.</summary>
public sealed class LitigationCaseSourceReport
{
    public long LitigationCaseSourceReportId { get; set; }
    public long LitigationCaseId { get; set; }
    public LitigationCase? Case { get; set; }
    public long LitigationSearchJobId { get; set; }
    public LitigationSearchJob? SearchJob { get; set; }

    /// <summary>The persisting job's RawResponseHash at the time this link was created — see this type's
    /// remarks for why job id alone is not enough to identify one report/run.</summary>
    public string ReportHash { get; set; } = string.Empty;

    public DateTime FirstSeenUtc { get; set; }
}
