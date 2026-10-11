namespace MCAROC_Analysis.Services.LitigationModule.Store;

// The litigation module's own tables, in the `litigation` SQL schema (plan section 12.3). Nothing outside Services/LitigationModule may
// reference these types: consumers go through the module's reader and writer. An architecture test enforces it.

public enum ModuleScopeKind { Request = 1, Assignment = 2 }

public enum ModuleSubjectKind { Company = 1, Individual = 2 }

/// <summary>A case in the registry: a fact independent of who is asking. <see cref="CaseId"/> is permanent; the legacy references are kept for ever so
/// every old URL, printed <c>CaseRowId</c> and saved reference still resolves (plan section 12.6 item 5).</summary>
public sealed class ModuleCase
{
    public long CaseId { get; set; }

    /// <summary>The legacy request-side row (<c>LitigationCase.LitigationCaseId</c>), when the case came from there.</summary>
    public long? LegacyLitigationCaseId { get; set; }

    /// <summary>The legacy pre-login record: the job and its <c>CaseRowId</c>. Both or neither.</summary>
    public long? LegacyJobId { get; set; }
    public string? LegacyCaseRowId { get; set; }

    /// <summary>The shared case key, only ever minted by <c>CaseKeyGate</c>; null for a case that did not pass the gate.</summary>
    public string? CaseKey { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>The party being checked. Shared by every scope that checks it; owns nothing but its identity.</summary>
public sealed class ModuleSubject
{
    public long SubjectId { get; set; }
    public ModuleSubjectKind Kind { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>Name variants and identifiers (CIN, PAN ...) as JSON; read by the matching and refresh code, never shown raw.</summary>
    public string? IdentifiersJson { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>One consumer instance: a request or a pre-login assignment. Refers to exactly one subject.</summary>
public sealed class ModuleScope
{
    public long ScopeId { get; set; }
    public ModuleScopeKind Kind { get; set; }

    /// <summary>The consumer's own id: the request id, or the pre-login job id.</summary>
    public long ExternalId { get; set; }

    public long SubjectId { get; set; }
    public ModuleSubject? Subject { get; set; }

    /// <summary>Bumped by every membership or overlay change; the optimistic check for analyst edits and bulk updates (plan section 12.3).</summary>
    public long Version { get; set; }

    /// <summary>The monotonic counter behind the scope's aliases (<c>C-1</c>, <c>C-2</c> ...). Never reused, never reset.</summary>
    public int NextAliasIndex { get; set; } = 1;

    public DateTime CreatedUtc { get; set; }
}

/// <summary>The association of a case with a scope, and everything true for that scope only. Unique on (scope, case) and on (scope, alias).</summary>
public sealed class ModuleScopeCase
{
    public long ScopeCaseId { get; set; }
    public long ScopeId { get; set; }
    public ModuleScope? Scope { get; set; }
    public long CaseId { get; set; }
    public ModuleCase? Case { get; set; }

    /// <summary>The scope's display id for the case. For a migrated pre-login record this is its existing <c>CaseRowId</c>, verbatim.</summary>
    public string Alias { get; set; } = string.Empty;
    public int AliasIndex { get; set; }

    /// <summary>When this scope's own vendor imports last listed the case (feed membership); null when never from a feed.</summary>
    public DateTime? FeedLastSeenUtc { get; set; }

    public bool IsHidden { get; set; }
    public bool IsExcluded { get; set; }
    public string? ExclusionReason { get; set; }
    public string? AnalystNote { get; set; }

    /// <summary>The analyst's pinned field edits, with who and when. They win over portal and vendor inside this scope only.</summary>
    public string? PinnedFieldsJson { get; set; }

    public bool IncludedInReport { get; set; } = true;

    /// <summary>Old aliases that still resolve to this case after a merge, as a JSON array.</summary>
    public string? RedirectAliasesJson { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? UpdatedUtc { get; set; }
}

public enum BridgeEventKind { CaseUpserted = 1 }

/// <summary>The transactional outbox of the bridge (plan section 12.5). A legacy writer adds a row in the same transaction as its own write; the bridge processor applies
/// the rows idempotently and in order. A row that keeps failing is dead-lettered, never dropped.</summary>
public sealed class ModuleBridgeOutbox
{
    public long OutboxId { get; set; }
    public BridgeEventKind Kind { get; set; }

    public long LegacyLitigationCaseId { get; set; }
    public MCAROC_Analysis.Data.Entities.LitigationCase? LegacyCase { get; set; }
    public long RequestId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? ProcessedUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime? DeadLetteredUtc { get; set; }
}

/// <summary>One portal or vendor observation of a case, append-only (plan section 4.1). No raw response is kept: only the compact parsed snapshot, the outcome and the
/// parser version. An observation is never updated after it is written.</summary>
public sealed class ModuleCaseObservation
{
    public long ObservationId { get; set; }
    public long CaseId { get; set; }
    public ModuleCase? Case { get; set; }

    /// <summary>1 vendor, 2 DRT portal, 3 consumer portal (see <c>ObservationSource</c>).</summary>
    public int Source { get; set; }

    /// <summary>1 found, 2 not found, 3 error, 4 identity rejected (see <c>ObservationOutcome</c>).</summary>
    public int Outcome { get; set; }

    /// <summary>True only for a found response whose identity check passed.</summary>
    public bool Verified { get; set; }

    public int ParserVersion { get; set; }
    public DateTime RetrievedUtc { get; set; }

    /// <summary>A source-side change counter when the source has one; null otherwise.</summary>
    public long? EventWatermark { get; set; }

    /// <summary>Set only from a real timestamp the source itself returned. Null in v1: no current source gives one.</summary>
    public DateTime? SourceUpdatedUtc { get; set; }

    /// <summary>The compact parsed snapshot as JSON (<c>ParsedCaseSnapshot</c>); null for not-found and error outcomes.</summary>
    public string? SnapshotJson { get; set; }

    /// <summary>What the projection did with it (<c>ProjectionOutcome</c>), recorded for audit.</summary>
    public string? AppliedAs { get; set; }

    public DateTime CreatedUtc { get; set; }
}

/// <summary>The verified projection of a case: its last verified observations folded in field by field. One row per case. <see cref="Version"/> is the compare-and-set token
/// for publication: a stale writer's save fails and it re-reads (plan section 4.1).</summary>
public sealed class ModuleCaseLive
{
    public long CaseId { get; set; }
    public ModuleCase? Case { get; set; }

    public string? Status { get; set; }
    public string? Stage { get; set; }
    public string? Court { get; set; }
    public string? FilingDate { get; set; }
    public string? LastHearingDate { get; set; }
    public string? NextHearingDate { get; set; }
    public string? DecisionDate { get; set; }
    public string? DisposalNature { get; set; }
    public string? SuitAmount { get; set; }
    public int HearingCount { get; set; }

    public DateTime? LastRetrievedUtc { get; set; }
    public long? LastObservationId { get; set; }

    /// <summary>Per-field provenance and confirmed-empty marks as JSON (<c>FieldSource</c> by field name).</summary>
    public string? FieldSourcesJson { get; set; }

    /// <summary>A change held until a second observation agrees (<c>status:PENDING</c>, <c>history:4</c>); null when nothing is held.</summary>
    public string? HeldChange { get; set; }

    public long Version { get; set; }
    public DateTime UpdatedUtc { get; set; }
}

/// <summary>Is a request's company really a party to a case? Per request and case, separate from the registry's fetch status (plan section 4.3). Only a <c>Verified</c> link lets
/// portal data be published into that request's case; a Rejected link never blocks another request that legitimately is a party.</summary>
public sealed class ModuleRequestLink
{
    public long LinkId { get; set; }
    public long RequestId { get; set; }
    public long CaseId { get; set; }
    public ModuleCase? Case { get; set; }

    /// <summary>0 verified, 1 ambiguous, 2 rejected (see <c>PartyMatchOutcome</c>).</summary>
    public int Verdict { get; set; }

    /// <summary>Which rule decided (<c>exact</c>, <c>exact-former-name</c>, <c>legal-form-differs</c>, <c>extra-words</c>, <c>no-match</c>).</summary>
    public string? Rule { get; set; }

    public string? MatchedParty { get; set; }
    public string? MatchedSide { get; set; }

    /// <summary>The stored direction disagrees with the side the portal lists the company on. Recorded, never resolved by guessing.</summary>
    public bool RoleConflict { get; set; }

    public string? RoleNote { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
