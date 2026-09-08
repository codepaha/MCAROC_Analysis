namespace MCAROC_Analysis.Data.Entities;

/// <summary>One PDF extracted from a filing's nested zip.</summary>
public class McaFilingDocument
{
    public long FilingDocumentId { get; set; }
    public long FilingId { get; set; }
    public McaFiling? Filing { get; set; }
    public long BatchId { get; set; }
    public long RequestId { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;
    /// <summary>Raw subfolder within the nested zip, e.g. "Other Documents Eform" — secondary
    /// classification signal.</summary>
    public string SourceFolder { get; set; } = string.Empty;
    public string StoragePath { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;

    /// <summary>Set when this hash already exists elsewhere in the batch; duplicates skip text
    /// extraction/AI and reuse the canonical document's results.</summary>
    public long? DuplicateOfDocumentId { get; set; }

    public int PageCount { get; set; }

    // Classification — every decision is auditable, not just its outcome.
    public FilingCategory Category { get; set; } = FilingCategory.Unclassified;
    public string? FormType { get; set; }
    public ClassificationConfidence ClassificationConfidence { get; set; }
    public string ClassificationMethod { get; set; } = string.Empty;
    public string? MatchedRule { get; set; }

    // Processing — ProcessingStatus is the single source of truth for pipeline stage.
    public FilingDocumentProcessingStatus ProcessingStatus { get; set; } = FilingDocumentProcessingStatus.Discovered;
    public TextExtractionMethod TextExtractionMethod { get; set; } = TextExtractionMethod.None;
    public AiExtractionStatus AiExtractionStatus { get; set; } = AiExtractionStatus.NotApplicable;

    // Text — stored as a file, not a DB blob; some documents run 300K+ characters.
    public string? ExtractedTextPath { get; set; }
    public int ExtractedCharCount { get; set; }
    public int NativePageCount { get; set; }
    public int OcrPageCount { get; set; }

    // Reliability
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? ProcessingStartedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Review
    public bool ManualReviewRequired { get; set; }
    public string? ManualReviewReason { get; set; }
}
