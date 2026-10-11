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
