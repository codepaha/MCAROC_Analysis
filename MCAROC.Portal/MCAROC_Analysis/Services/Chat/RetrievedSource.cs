namespace MCAROC_Analysis.Services.Chat;

public enum SourceType
{
    DocumentChunk,
    StructuredFact,
    LitigationChunk
}

/// <summary>One [FACT]/[SOURCE] entry offered to the model in a prompt. Tag ("F1", "D2", "L3", ...) is assigned
/// centrally by RetrievalContextBuilder once structured facts, document chunks and litigation chunks are
/// combined, so numbering is consistent across all three kinds. Carries everything needed both to render the
/// prompt block (Text) and to resolve a citation later (ChunkId/DocumentName/PageNumber for DocumentChunk;
/// EntityType/EntityId for StructuredFact; ChunkId/DocumentId(=LitigationOrderDocumentId)/LitigationCaseId/
/// LitigationCaseOrderId/PageNumber for LitigationChunk) — a structured-fact citation is a real reference, not
/// just a label. LitigationChunk reuses ChunkId/DocumentName/PageNumber/DocumentId rather than inventing
/// parallel fields for the parts that mean the same thing (a chunk id, a display name, a page, an owning
/// document id) — only LitigationCaseId/LitigationCaseOrderId are genuinely litigation-specific, needed so a
/// citation can identify "the precise case, order and page" per #244/LIT-04's acceptance criterion.</summary>
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
    long? DocumentId = null,
    long? LitigationCaseId = null,
    long? LitigationCaseOrderId = null);
