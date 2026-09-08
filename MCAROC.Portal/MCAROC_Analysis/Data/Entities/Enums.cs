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
    Cancelled
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
