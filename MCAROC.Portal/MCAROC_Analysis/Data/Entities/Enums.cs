namespace MCAROC_Analysis.Data.Entities;

public enum EntityType
{
    Company,
    LLP
}

public enum RequestStatus
{
    Created,
    DocumentsUploaded,
    Validating,
    ExtractionInProgress,
    DataExtracted,
    ValidationFailed,
    ExtractionFailed,
    Cancelled,
    AiAnalysisInProgress,
    AnalysisCompleted,
    AiAnalysisFailed
}

public enum DocumentType
{
    McaRocReport,
    ChargeReport,
    FinancialReport,
    McaFilingsArchive,
    Other
}

public enum DocumentUploadStatus
{
    Uploaded,
    ValidationFailed,
    Processed,
    Quarantined
}

public enum IngestionRunStatus
{
    Running,
    CompletedClean,
    CompletedWithWarnings,
    Failed
}

public enum IssueSeverity
{
    Warning,
    Error
}

public enum ChargeEventType
{
    Creation,
    Modification,
    Satisfaction
}

public enum ChargeEventMatchConfidence
{
    Exact,
    Partial,
    SerialNumberOnly,
    Unmatched
}

public enum ShareholdingSourceType
{
    DirectorShareholding,
    MajorShareholding
}

public enum LitigationMatchStatus
{
    Confirmed,
    Probable,
    Uncertain
}

public enum ReviewPriority
{
    Low,
    Medium,
    High
}

public enum AnalysisRunStatus
{
    Running,
    Completed,
    CompletedWithErrors,
    Failed
}

public enum FindingSection
{
    CompanyProfile,
    Directors,
    DirectorNetwork,
    Ownership,
    Financial,
    Charges,
    Msme,
    Gst,
    Epfo,
    Auditor,
    Litigation,
    CrossSection
}

public enum FindingSeverity
{
    Positive,
    Watch,
    Review,
    Critical
}

/// <summary>Whether a finding describes something true right now, something that already happened/was
/// resolved, or an observed pattern across periods. ReviewPriorityCalculator only escalates on
/// Current/Trend — a Historical Critical finding alone must never drive OverallReviewPriority to High.</summary>
public enum TemporalStatus
{
    Current,
    Historical,
    Trend
}

// ── Phase 6 ─────────────────────────────────────────────────────────────────────

/// <summary>Standalone vs Consolidated financial statements. Consolidated rows live in the same
/// FinancialYearData / AuditorObservation tables; the rule engine and default RAG facts read Standalone only.</summary>
public enum FinancialBasis
{
    Standalone,
    Consolidated
}

/// <summary>Normalized from the "Related Corporates" sheet's explicit Relationship text — never inferred
/// from holding %.</summary>
public enum RelationshipType
{
    Subsidiary,
    Associate,
    JointVenture,
    Holding,
    Other
}

public enum ComplianceRecordType
{
    NameRemoval,
    NameRestoration,
    Bifr,
    Cdr,
    SuitFiled,
    Other
}

/// <summary>Mathematical position of the company value vs the peer median for a metric — NOT a
/// good/bad judgement (see PeerComparisonDisplayRules for desirability per metric).</summary>
public enum PeerPosition
{
    NotComparable,
    Below,
    InLine,
    Above
}

public enum PeerMetricDirection
{
    Unknown,
    Neutral,
    HigherIsBetter,
    LowerIsBetter
}

// ── Phase 6: charge security classification ─────────────────────────────────────

public enum FacilityType
{
    CashCredit,
    BankGuarantee,
    LetterOfCredit,
    BuyersCredit,
    TermLoan,
    WorkingCapital,
    Other
}

public enum SecurityType
{
    CurrentAssets,
    MovableFixedAssets,
    ImmovableProperty,
    BookDebts,
    FixedDeposit,
    Vehicle,
    Other
}

/// <summary>Priority/sharing rank of a charge over an asset. Kept separate from ChargeArrangement —
/// a charge can be PariPassu ranked AND a Consortium arrangement.</summary>
public enum ChargeRanking
{
    Unknown,
    Exclusive,
    FirstCharge,
    SecondCharge,
    PariPassu,
    Subservient
}

public enum ChargeArrangement
{
    Unknown,
    Sole,
    Consortium,
    JointCharge,
    MultipleLenders
}

/// <summary>Overall confidence of ChargeSecurityClassifier for one event. None = nothing matched
/// (the UI then shows raw source wording only, never an inferred badge).</summary>
public enum ChargeClassificationConfidence
{
    None,
    Low,
    Medium,
    High
}

/// <summary>Provenance of a litigation case's filed-by/filed-against role. Room for a structured
/// CompanyRole in a later phase.</summary>
public enum LitigationRoleSource
{
    Unknown,
    RuleEngine
}

/// <summary>Where a Litigation row came from. Today every row is <see cref="RocReport"/> (the MCA/ROC
/// workbook's Legal History sheet); a future manual "pull from lake" action adds <see cref="DataLake"/>
/// rows from the internal 1.5-billion-record litigation data lake without disturbing the existing set.</summary>
public enum LitigationSource
{
    RocReport,
    DataLake
}
