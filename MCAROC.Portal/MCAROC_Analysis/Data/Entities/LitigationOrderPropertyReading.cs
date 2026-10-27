namespace MCAROC_Analysis.Data.Entities;

/// <summary><see cref="NoPropertyPages"/> is terminal without a call: no page of the order may name a property
/// (<c>OrderPropertiesAi.MayNameProperty</c>), so there is nothing to read.</summary>
public enum LitigationOrderPropertyReadingStatus { Pending, InProgress, Completed, Failed, NoPropertyPages }

/// <summary>The properties one court order names (#554), read by <c>OrderPropertiesAi</c> from the order's pages that may name one. One row per
/// <see cref="LitigationOrderDocument"/>. <see cref="InputHash"/> covers everything the prompt is built from (the pages sent, the case's court,
/// type and parties, the company's names, the prompt version), so an order whose input another order already had is answered from that
/// reading without a call. The order text is not copied here: it is read from the document when the reading runs, and stays under the
/// document's own retention.</summary>
public class LitigationOrderPropertyReading
{
    public long LitigationOrderPropertyReadingId { get; set; }
    public long LitigationOrderDocumentId { get; set; }
    public long RequestId { get; set; }

    public string InputHash { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;

    public LitigationOrderPropertyReadingStatus Status { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseExpiresUtc { get; set; }
    public DateTime? NextAttemptUtc { get; set; }

    /// <summary>JSON array of the page numbers sent.</summary>
    public string? PagesSentJson { get; set; }

    /// <summary>Set when the result was taken from another reading with the same input (no call made).</summary>
    public long? ReusedFromReadingId { get; set; }

    public string? RawResponseJson { get; set; }
    public string? ResponseHash { get; set; }

    /// <summary>The validated <c>OrderPropertiesResult</c>: only values that passed the grounding checks.</summary>
    public string? ResultJson { get; set; }

    /// <summary>JSON array of what the checks dropped and why, for audit.</summary>
    public string? RejectedFieldsJson { get; set; }

    public string? FailureReason { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }

    public LitigationOrderDocument? Document { get; set; }

    public bool IsTerminal => Status is LitigationOrderPropertyReadingStatus.Completed or LitigationOrderPropertyReadingStatus.Failed
        or LitigationOrderPropertyReadingStatus.NoPropertyPages;
}
