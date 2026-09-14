namespace MCAROC_Analysis.Data.Entities;

public enum DocumentLinkKind
{
    Source,
    Supports,
    Contradicts,
    Duplicate,
    Supersedes
}

public enum DocumentDataLinkStatus
{
    AutoAccepted,
    PendingReview,
    Confirmed,
    Rejected,
    SupersededByDuplicate,
    UnlinkedNoCandidate,
    UnlinkedAmbiguous,
    UnlinkedInsufficientText,
    UnlinkedOutOfScope,
    InvalidDocument
}

public enum DocumentLinkMatchMethod
{
    DirectProvenance,
    ChargeCompositeKey,
    FilingMetadata,
    FinancialPeriod,
    TextEvidence,
    Manual
}

public enum DocumentLinkConfidence
{
    High,
    Medium,
    Low
}

public enum ChargeDateMatchMode
{
    None,
    EventDateMatched,
    FilingDateMatched,
    BothDatesMatched
}
