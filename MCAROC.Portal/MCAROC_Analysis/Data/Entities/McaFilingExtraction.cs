namespace MCAROC_Analysis.Data.Entities;

/// <summary>Gemini's structured extraction result. Normally one per McaFiling (extraction runs per filing,
/// grouping a form with its attachments); FilingDocumentId is used only when a filing is large enough to
/// need document-level extraction with later consolidation. Exactly one of FilingId/FilingDocumentId is set.</summary>
public class McaFilingExtraction
{
    public long ExtractionId { get; set; }

    public long? FilingId { get; set; }
    public McaFiling? Filing { get; set; }
    public long? FilingDocumentId { get; set; }
    public McaFilingDocument? FilingDocument { get; set; }

    public string Model { get; set; } = "gemini-2.5-flash-lite";
    public string PromptVersion { get; set; } = "1.0";
    public string SchemaName { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = "1.0";

    public string? ExtractedJson { get; set; }
    /// <summary>Always retained, even on validation failure — needed for diagnostics.</summary>
    public string? RawModelResponse { get; set; }

    public ExtractionValidationStatus ValidationStatus { get; set; }
    public string? ValidationErrors { get; set; }

    public ExtractionStatus Status { get; set; }
    public string? FailureReason { get; set; }

    public DateTime ExtractedAt { get; set; }
}
