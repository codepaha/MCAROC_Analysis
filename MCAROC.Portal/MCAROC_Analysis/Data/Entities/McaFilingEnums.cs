namespace MCAROC_Analysis.Data.Entities;

public enum FilingBatchStatus
{
    Uploaded,
    Unpacking,
    Indexing,
    Processing,
    Completed,
    CompletedWithErrors,
    Failed
}

public enum FilingCategory
{
    Charge,
    Compliance,
    Constitutional,
    Financial,
    Unclassified
}

public enum ClassificationConfidence
{
    High,
    Medium,
    Low
}

public enum FilingDocumentProcessingStatus
{
    Discovered,
    Classified,
    TextExtracting,
    TextExtracted,
    AiQueued,
    AiProcessing,
    Completed,
    Failed,
    Skipped,
    PasswordProtected,
    CorruptPdf,
    UnsupportedPdf
}

public enum TextExtractionMethod
{
    None,
    Native,
    Ocr,
    Mixed
}

public enum AiExtractionStatus
{
    NotApplicable,
    Pending,
    Success,
    Failed
}

public enum ExtractionValidationStatus
{
    Valid,
    Invalid,
    PartialWarnings
}

public enum ExtractionStatus
{
    Success,
    Failed
}
