using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationModule.Discovery;

/// <summary>Status of a discovered candidate through the intake pipeline.</summary>
public enum IntakeCandidateStatus
{
    /// <summary>Genuinely new verified case: detail fetched, case row created, linked, observed and published.</summary>
    Created,

    /// <summary>Candidate matches an existing case of the request by gate-complete CaseKey; skipped without duplication.</summary>
    AlreadyExists,

    /// <summary>Party match rejected under the whole-name rule (audit logged, not added).</summary>
    PartyRejected,

    /// <summary>Party match ambiguous under the whole-name rule (audit logged, not added).</summary>
    PartyAmbiguous,

    /// <summary>Case number or court failed CaseKeyGate (partial/unresolved); logged as possible duplicate, never keyed or merged.</summary>
    FailedGatePartial,

    /// <summary>Detail call to portal returned not found, error, or unavailable.</summary>
    DetailFetchFailed,

    /// <summary>Detail call body returned an identity differing from what was looked up.</summary>
    IdentityMismatch
}

/// <summary>Result of processing a single candidate through the intake pipeline.</summary>
public sealed record DiscoveredCaseIntakeItemResult(
    DiscoveredCaseCandidate Candidate,
    IntakeCandidateStatus Status,
    string? CaseKey,
    long? LegacyLitigationCaseId,
    long? ModuleCaseId,
    bool IsDisposed,
    LiveRefreshAction RefreshAction,
    string? Note);

/// <summary>Result of an intake batch run for a request.</summary>
public sealed record PartyDiscoveryIntakeBatchResult(
    long RequestId,
    int TotalCandidatesProcessed,
    int CreatedCount,
    int AlreadyExistingCount,
    int RejectedCount,
    int PossibleDuplicatesCount,
    long? NewLiveDataVersion,
    IReadOnlyList<DiscoveredCaseIntakeItemResult> Items);

/// <summary>Intake pipeline contract for turning discovered candidates into verified request cases (plan section 4.4 flow 2).</summary>
public interface IPartyDiscoveryIntakeService
{
    /// <summary>Processes a collection of discovered candidates for a request: verifies parties, de-duplicates by CaseKey,
    /// calls details for genuinely new cases, takes per-case lock, publishes live projections, routes disposed cases to orders only,
    /// and bumps LiveDataVersion once per batch.</summary>
    Task<PartyDiscoveryIntakeBatchResult> IntakeCandidatesAsync(
        long requestId,
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames,
        IEnumerable<DiscoveredCaseCandidate> candidates,
        CancellationToken ct = default);

    /// <summary>Runs party discovery across portals and intakes the resulting candidates in one flow.</summary>
    Task<PartyDiscoveryIntakeBatchResult> DiscoverAndIntakeAsync(
        long requestId,
        IEnumerable<string> companyNames,
        IEnumerable<string>? formerNames = null,
        CancellationToken ct = default);
}

