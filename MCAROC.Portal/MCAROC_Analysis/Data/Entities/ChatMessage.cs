namespace MCAROC_Analysis.Data.Entities;

public enum ChatRole
{
    User,
    Assistant
}

public enum ChatMessageStatus
{
    Success,
    Failed
}

/// <summary>One turn in a chat session. Assistant messages carry both RetrievedSourcesJson (everything
/// actually retrieved this turn, with relevance scores — structured facts and document chunks alike) and
/// CitedSourcesJson (the validated subset the model actually cited) as two separate fields, so "the
/// retriever found it but the model ignored it" is distinguishable from "the retriever never found it" when
/// debugging or tuning retrieval — never inferred from one list alone.</summary>
public class ChatMessage
{
    public long ChatMessageId { get; set; }
    public long ChatSessionId { get; set; }

    public ChatRole Role { get; set; }
    public string MessageText { get; set; } = string.Empty;

    /// <summary>Assistant messages only. Every fact/chunk tag ([F1], [D1], ...) included in the prompt,
    /// each with its relevance score (cosine distance for document chunks).</summary>
    public string? RetrievedSourcesJson { get; set; }

    /// <summary>Assistant messages only. The validated subset of tags the model actually cited, resolved to
    /// full citation objects: {"sourceType":"DocumentChunk","chunkId":...,"documentName":...,"page":...} or
    /// {"sourceType":"StructuredFact","entityType":...,"entityId":...,"label":...}.</summary>
    public string? CitedSourcesJson { get; set; }

    public string? Model { get; set; }
    public string? PromptVersion { get; set; }
    public string? EmbeddingModel { get; set; }
    public int? TopK { get; set; }

    /// <summary>Assistant messages only — if the Gemini call itself failed, the turn is still representable
    /// (the user's question persists) rather than silently vanishing.</summary>
    public ChatMessageStatus? Status { get; set; }

    public DateTime CreatedDate { get; set; }
}
