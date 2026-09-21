using Microsoft.Data.SqlTypes;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>One page (or a piece of an oversized page) of a <see cref="LitigationOrderDocument"/>'s extracted
/// text, embedded for vector search — the parallel, litigation-specific counterpart to <see
/// cref="DocumentChunk"/> that #244/LIT-04 requires (never fabricating an MCA filing id for a litigation
/// order — see docs/litigation-data-lake-integration.md's "New durable records"). <see cref="RequestId"/> is
/// denormalized for the exact same reason as <see cref="DocumentChunk.RequestId"/>: every retrieval query
/// filters on it directly, so cross-request leakage is structurally harder, not just procedurally avoided —
/// see <c>LitigationDocumentRetriever</c>.
///
/// <see cref="LitigationCaseId"/>/<see cref="CaseNumber"/>/<see cref="Cnr"/>/<see cref="Court"/>/
/// <see cref="OrderType"/>/<see cref="OrderDate"/> are denormalized from the owning case/order for the same
/// reason <see cref="DocumentChunk"/> denormalizes <c>Srn</c>/<c>Category</c>/<c>FormType</c>/<c>DocumentName</c>:
/// a citation needs to identify "the precise case, order and page" (epic #239's #244 acceptance criterion)
/// without a join per chunk. Retained independently of the source PDF's own retention/expiry — an expired or
/// since-purged original never removes an already-indexed chunk's text or provenance (the second #244
/// acceptance criterion); nothing in this codebase deletes a chunk except a genuine re-chunk of the same
/// order document (old chunk set replaced, not appended — see the orchestrator).</summary>
public class LitigationOrderChunk
{
    public long LitigationOrderChunkId { get; set; }
    public long RequestId { get; set; }
    public long LitigationOrderDocumentId { get; set; }
    public long LitigationCaseOrderId { get; set; }
    public long LitigationCaseId { get; set; }

    public string? CaseNumber { get; set; }
    public string? Cnr { get; set; }
    public string? Court { get; set; }
    public string? OrderType { get; set; }
    public string? OrderDate { get; set; }

    public int ChunkIndex { get; set; }

    /// <summary>A chunk is always exactly one page or a piece of one page — never spans pages, so a
    /// citation's page number is always exact. Same guarantee and mechanism as <see cref="DocumentChunk.PageNumber"/>:
    /// derived from the <c>--- Page N (native|OCR) ---</c> markers <c>PdfTextExtractor</c> writes into
    /// <see cref="LitigationOrderDocument.ExtractedText"/>.</summary>
    public int PageNumber { get; set; }
    public string ChunkText { get; set; } = string.Empty;

    public SqlVector<float> Embedding { get; set; }

    /// <summary>Recorded so a future model/dimension/chunking-strategy change can identify which chunks need
    /// re-embedding — exactly one active chunk set per order document (re-chunking replaces rather than
    /// versions in place), so these are for audit/rebuild-detection, not side-by-side retrieval.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;
    public int EmbeddingDimensions { get; set; }
    public string ChunkingVersion { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }
}
