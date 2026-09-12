namespace MCAROC_Analysis.Services.Chat;

public enum SourceType
{
    DocumentChunk,
    StructuredFact
}

/// <summary>One [FACT]/[SOURCE] entry offered to the model in a prompt. Tag ("F1", "D2", ...) is assigned
/// centrally by RetrievalContextBuilder once structured facts and document chunks are combined, so numbering
/// is consistent across both kinds. Carries everything needed both to render the prompt block (Text) and to
/// resolve a citation later (ChunkId/DocumentName/PageNumber for DocumentChunk; EntityType/EntityId for
/// StructuredFact) — a structured-fact citation is a real reference, not just a label.</summary>
public record RetrievedSource(
    string Tag,
    SourceType Type,
    string Text,
    string DisplayLabel,
    double? RelevanceScore = null,
    long? ChunkId = null,
    string? DocumentName = null,
    int? PageNumber = null,
    string? EntityType = null,
    long? EntityId = null,
    long? DocumentId = null);
