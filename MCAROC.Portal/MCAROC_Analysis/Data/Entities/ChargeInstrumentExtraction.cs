namespace MCAROC_Analysis.Data.Entities;

public enum ChargeInstrumentExtractionStatus { Pending, InProgress, Completed, Failed }

/// <summary>#364 part 2: the property passages Gemini quoted from one charge document — a deed, instrument, scanned CHG-1
/// or letter linked to a charge (<see cref="ChargeDocumentLink"/>) whose property is not in readable form data. One row per
/// document and prompt version. <see cref="ResultJson"/> holds only passages that occur verbatim in the document's own text,
/// each with the page it starts on, so it is the schedule as filed with a page link as provenance.</summary>
public class ChargeInstrumentExtraction
{
    public long ChargeInstrumentExtractionId { get; set; }
    public long RequestId { get; set; }
    public long FilingDocumentId { get; set; }
    public McaFilingDocument? FilingDocument { get; set; }

    public string PromptVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    public ChargeInstrumentExtractionStatus Status { get; set; } = ChargeInstrumentExtractionStatus.Pending;
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }

    public string? RawResponseJson { get; set; }
    /// <summary>The validated passages (<c>ChargeInstrumentAiResult</c>): verbatim text, kind and page.</summary>
    public string? ResultJson { get; set; }
    public string? RejectedFieldsJson { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>When this result was copied from an earlier extraction of the same PDF in this request (a refreshed batch
    /// holds the same files), no model call was made.</summary>
    public long? ReusedFromExtractionId { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
