namespace MCAROC_Analysis.Data.Entities;

/// <summary>One call to a paid model (a generation or an embedding batch): what it was for, which model, and the usage the provider reported.
/// Rows are written by <c>AiUsageLog</c> as the calls happen so a request's real spend can be read back, instead of being estimated from document
/// sizes. <see cref="RequestId"/> is null for work that belongs to no single assignment.</summary>
public class AiUsageRecord
{
    public long AiUsageRecordId { get; set; }

    public long? RequestId { get; set; }

    /// <summary>What the call was for (<c>AiUsagePurposes</c>); the cost report groups on this.</summary>
    public string Purpose { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }

    /// <summary>Reasoning tokens. They are billed at the output rate but reported separately from the visible answer.</summary>
    public int ThoughtTokens { get; set; }

    /// <summary>Embedding calls report billable characters; null for generations.</summary>
    public long? BillableCharacters { get; set; }

    public bool Succeeded { get; set; } = true;

    /// <summary>True when the token counts are an estimate (an embedding batch the provider gave no token count for, or a generation with no usage
    /// metadata), so the report can show the cost as approximate instead of as recorded.</summary>
    public bool TokensEstimated { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
