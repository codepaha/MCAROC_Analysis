using Microsoft.Data.SqlTypes;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>One page (or a piece of an oversized page) of a McaFilingDocument's extracted text, embedded
/// for vector search. RequestId is denormalized specifically so every retrieval query can filter on it
/// directly — the mandatory per-request isolation boundary for the "Ask Documents" chatbot (chatbot.md's
/// BFSI cross-company-leakage requirement). A duplicate McaFilingDocument (same file hash, different
/// FilingId/Srn) gets its own DocumentChunk rows reusing the canonical document's already-computed
/// embedding values — deduplication only saves the embedding API call, never citation provenance.</summary>
public class DocumentChunk
{
    public long ChunkId { get; set; }
    public long RequestId { get; set; }
    public long FilingDocumentId { get; set; }
    public long FilingId { get; set; }
    public long BatchId { get; set; }

    public string Srn { get; set; } = string.Empty;
    public FilingCategory Category { get; set; }
    public string? FormType { get; set; }
    public string DocumentName { get; set; } = string.Empty;

    public int ChunkIndex { get; set; }
    /// <summary>A chunk is always exactly one page or a piece of one page — never spans pages, so a
    /// citation's page number is always exact.</summary>
    public int PageNumber { get; set; }
    public string ChunkText { get; set; } = string.Empty;

    public SqlVector<float> Embedding { get; set; }

    /// <summary>Recorded so a future model/dimension/chunking-strategy change can identify which chunks
    /// need re-embedding — Phase 4 keeps exactly one active chunk set per document (re-chunking replaces
    /// rather than versions in place), so these are for audit/rebuild-detection, not side-by-side retrieval.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;
    public int EmbeddingDimensions { get; set; }
    public string ChunkingVersion { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }
}
