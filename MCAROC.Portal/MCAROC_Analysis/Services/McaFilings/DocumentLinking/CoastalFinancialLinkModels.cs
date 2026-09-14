using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public enum PilotFinancialLinkOutcome
{
    AutoAccepted,
    PendingReview,
    UnlinkedNoCandidate,
    UnlinkedOutOfScope,
    ManifestDuplicateBypassed
}

public enum PilotFinancialLinkReason
{
    ExactMatchReportingPeriodAndStatementValue,
    ExactMatchReportingPeriod,
    InferredCashFlowPeriod,
    PeriodNotFoundInWorkbook,
    MissingReportingPeriod,
    NonFinancialDocument,
    ManifestDuplicate,
    AmbiguousFinancialYear,
    ConflictingBasisEvidence,
    MissingBasisEvidence,
    StatementValueMismatch
}

public class CoastalFinancialLinkResultEntry
{
    public string OuterEntryFullPath { get; set; } = string.Empty;
    public string NestedEntryRelativePath { get; set; } = string.Empty;
    public string Sha256Hex { get; set; } = string.Empty;

    public PilotFinancialLinkOutcome Outcome { get; set; }
    public PilotFinancialLinkReason Reason { get; set; }

    public int? MatchedFinancialYear { get; set; }
    public FinancialBasis? MatchedBasis { get; set; }

    public FinancialTargetKind? TargetKind { get; set; }
    public long? TargetEntityId { get; set; }
    public TargetSourceCoordinates? TargetCoordinates { get; set; }

    public string? TargetLineItem { get; set; }
    public decimal? MatchedValue { get; set; }
    public int? EvidencePageNumber { get; set; }
    public string? EvidenceTextQuote { get; set; }

    public string EvidenceJson { get; set; } = string.Empty;

    public bool IsCanonical { get; set; }
    public string CanonicalOuterEntryFullPath { get; set; } = string.Empty;
    public string CanonicalNestedEntryRelativePath { get; set; } = string.Empty;
}

public class CoastalFinancialLinkResult
{
    public IReadOnlyList<CoastalFinancialLinkResultEntry> Entries { get; init; } = [];

    public int TotalEntries => Entries.Count;
    public int ManifestDuplicateCount => Entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.ManifestDuplicateBypassed);
    public int OutOfScopeCount => Entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.UnlinkedOutOfScope);
    public int AutoAcceptedCount => Entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.AutoAccepted);
    public int PendingReviewCount => Entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.PendingReview);
    public int UnlinkedNoCandidateCount => Entries.Count(e => e.Outcome == PilotFinancialLinkOutcome.UnlinkedNoCandidate);
}
