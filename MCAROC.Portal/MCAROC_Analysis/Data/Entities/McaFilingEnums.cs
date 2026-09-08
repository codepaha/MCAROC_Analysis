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
    /// <summary>Text-extracted and AI-eligible, but not yet claimed by an ExtractFilingAsync run.</summary>
    Pending,
    /// <summary>Atomically claimed — a Gemini call is in flight (or was, until a crash). Distinct from
    /// Pending specifically so the claim UPDATE (WHERE AiExtractionStatus = Pending) has an unambiguous
    /// "not yet claimed" predicate to match against.</summary>
    InProgress,
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
