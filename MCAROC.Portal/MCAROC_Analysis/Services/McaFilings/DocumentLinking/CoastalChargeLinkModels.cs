using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// High-level linking outcome for a Coastal corpus PDF entry.
/// </summary>
public enum PilotLinkOutcome
{
    AutoAccepted,
    PendingReview,
    UnlinkedNoCandidate,
    UnlinkedOutOfScope,
    ManifestDuplicateBypassed
}

/// <summary>
/// Detailed justification for a linking outcome.
/// </summary>
public enum PilotLinkReason
{
    // Accepted
    ExactMatchBothDates,
    ExactMatchEventDate,
    ExactMatchFilingDate,

    // Pending Review
    DateMismatch,
    DateContradiction,
    EventTypeMismatch,
    AmbiguousMultipleEvents,
    ConflictingCorroboration,
    InvalidDateEvidence,

    // Unlinked
    MissingChargeId,
    ChargeNotFoundInWorkbook,
    NonChargeDocument,

    // Bypassed
    ManifestDuplicate
}

/// <summary>
/// Immutable result entry for a single PDF in the Coastal corpus.
/// </summary>
public sealed record CoastalChargeLinkResultEntry
{
    public required string OuterEntryFullPath { get; init; }
    public required string NestedEntryRelativePath { get; init; }
    public required string Sha256Hex { get; init; }
    public required PilotLinkOutcome Outcome { get; init; }
    public required PilotLinkReason Reason { get; init; }
    public long? MatchedRocChargeId { get; init; }
    public long? MatchedRocChargeEventId { get; init; }
    public string? MatchedEventSerialNumber { get; init; }
    public ChargeEventType? MatchedEventType { get; init; }
    public ChargeDateMatchMode DateMatchMode { get; init; } = ChargeDateMatchMode.None;
    public ChargeMatchFailureReason? MatchFailureReason { get; init; }
    public required bool IsCanonical { get; init; }
    public required string CanonicalOuterEntryFullPath { get; init; }
    public required string CanonicalNestedEntryRelativePath { get; init; }
    public required string EvidenceJson { get; init; }
}

/// <summary>
/// Result of executing the deterministic charge-link pass over the Coastal corpus.
/// </summary>
public sealed record CoastalChargeLinkResult(
    IReadOnlyList<CoastalChargeLinkResultEntry> Entries
)
{
    public int TotalPdfs => Entries.Count;
    public int AutoAcceptedCount => Entries.Count(e => e.Outcome == PilotLinkOutcome.AutoAccepted);
    public int PendingReviewCount => Entries.Count(e => e.Outcome == PilotLinkOutcome.PendingReview);
    public int UnlinkedNoCandidateCount => Entries.Count(e => e.Outcome == PilotLinkOutcome.UnlinkedNoCandidate);
    public int UnlinkedOutOfScopeCount => Entries.Count(e => e.Outcome == PilotLinkOutcome.UnlinkedOutOfScope);
    public int ManifestDuplicateBypassedCount => Entries.Count(e => e.Outcome == PilotLinkOutcome.ManifestDuplicateBypassed);
}
