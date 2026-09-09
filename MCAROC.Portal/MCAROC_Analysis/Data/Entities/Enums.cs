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
