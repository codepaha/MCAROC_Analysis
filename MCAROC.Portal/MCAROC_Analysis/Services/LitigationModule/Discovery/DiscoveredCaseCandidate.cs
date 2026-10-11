using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationModule.Discovery;

/// <summary>Source venue where a candidate was discovered.</summary>
public enum DiscoveredCaseSource
{
    Drt,
    Drat,
    Consumer
}

/// <summary>A case candidate discovered via party-name search across portals (plan section 4.4).
/// Contains full metadata returned by the portal search endpoint and party match evaluation.</summary>
public sealed record DiscoveredCaseCandidate(
    DiscoveredCaseSource Source,
    string SourceVenueId,
    string SourceVenueName,
    string CaseNumber,
    CaseNumberParts NormalizedCaseNumber,
    DateOnly? FilingDate,
    string? ApplicantOrComplainant,
    string? Respondent,
    PartyMatchResult MatchResult,
    string QueriedPartyName,
    bool IsAccepted,
    string? Stage = null,
    DateOnly? HearingDate = null,
    string? DiaryNo = null,
    string? StableSourceId = null,
    string? RawPayload = null);

/// <summary>Summary of a party discovery search operation across one or more tribunals/commissions.</summary>
public sealed record PartyDiscoveryResult(
    string SubjectName,
    IReadOnlyList<DiscoveredCaseCandidate> Candidates,
    int TotalCandidatesFound,
    int AcceptedCandidatesCount,
    int RejectedCandidatesCount,
    int VenuesSearchedCount,
    IReadOnlyList<string> Errors);

