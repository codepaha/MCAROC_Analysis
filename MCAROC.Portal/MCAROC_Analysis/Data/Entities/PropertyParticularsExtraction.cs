namespace MCAROC_Analysis.Data.Entities;

public enum PropertyParticularsExtractionStatus { Pending, InProgress, Completed, Failed }

/// <summary>One Gemini reading of a distinct charge "Particulars of Property Charged" text — split into separate
/// properties, each field copied from the source wording and checked against it (see
/// <c>PropertyParticularsAiValidator</c>). Keyed by <see cref="TextHash"/> (whitespace-normalised text plus the
/// charge's property-type column), not by request: the reading is derived only from that text, so identical
/// wording — which recurs across a charge's modifications and across requests — is extracted once. A request only
/// ever looks up hashes of texts in its own charges.
///
/// Same audit shape as the litigation AI tables: the exact prompt input (<see cref="SourceText"/>), the raw model
/// response, and the validated JSON are all kept; fields the model returned that are not present in the source are
/// listed in <see cref="RejectedFieldsJson"/> and never shown.</summary>
public class PropertyParticularsExtraction
{
    public long PropertyParticularsExtractionId { get; set; }
    public string TextHash { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    public string SourceText { get; set; } = string.Empty;
    public string? PropertyType { get; set; }

    public PropertyParticularsExtractionStatus Status { get; set; } = PropertyParticularsExtractionStatus.Pending;
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }

    public string? RawResponseJson { get; set; }
    public string? ResponseHash { get; set; }
    /// <summary>The validated, grounded properties (<c>PropertyParticularsAiResult</c>).</summary>
    public string? ExtractionJson { get; set; }
    public string? RejectedFieldsJson { get; set; }
    public string? FailureReason { get; set; }

    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
}
