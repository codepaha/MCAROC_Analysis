using System.Globalization;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record RetrievalContext(IReadOnlyList<RetrievedSource> Sources, string IndexingStatusLabel);

/// <summary>ChatService -> RetrievalContextBuilder -> StructuredFactsProvider + DocumentRetriever +
/// LitigationDocumentRetriever. This separation is what makes a real question-classifying router easy to slot
/// in later without restructuring ChatService — for now it always combines all three sources rather than
/// choosing one. The one routed source is #195's order-outcome lookup: a question asking for orders with a given
/// outcome also gets the exact, exhaustive LitigationOrderOutcomeQuery result ("O" sources) on top of retrieval.</summary>
public class RetrievalContextBuilder
{
    private readonly AppDbContext _db = null!;
    private readonly StructuredFactsProvider _structuredFacts = null!;
    private readonly DocumentRetriever _documentRetriever = null!;
    private readonly LitigationDocumentRetriever _litigationRetriever = null!;
    private readonly EmbeddingService _embeddingService = null!;
    private readonly LitigationOrderOutcomeQuery _orderOutcomes = null!;

    protected RetrievalContextBuilder() { }

    public RetrievalContextBuilder(
        AppDbContext db, StructuredFactsProvider structuredFacts, DocumentRetriever documentRetriever,
        LitigationDocumentRetriever litigationRetriever, EmbeddingService embeddingService, LitigationOrderOutcomeQuery orderOutcomes)
    {
        _db = db;
        _structuredFacts = structuredFacts;
        _documentRetriever = documentRetriever;
        _litigationRetriever = litigationRetriever;
        _embeddingService = embeddingService;
        _orderOutcomes = orderOutcomes;
    }

    public virtual async Task<RetrievalContext> BuildAsync(long requestId, string question, CancellationToken ct)
    {
        var knownLenders = await _db.RocCharges.Where(c => c.RequestId == requestId)
            .Select(c => c.LatestChargeHolderNormalized).Distinct().ToListAsync(ct);
        var knownSrns = await _db.McaFilings.Where(f => f.RequestId == requestId)
            .Select(f => f.Srn).Distinct().ToListAsync(ct);
        var hints = QuestionHintExtractor.Extract(question, knownLenders, knownSrns);

        var authoritativeBatch = await McaFilings.McaFilingBatchResolver.GetAuthoritativeBatchAsync(_db, requestId, ct);
        var facts = await _structuredFacts.BuildDigestAsync(requestId, hints, ct);

        // A request without an authoritative batch must never search its historical chunks. Likewise,
        // there is no reason to call Vertex for an embedding until that batch has indexed a chunk. The
        // embedding is shared between the MCA-filing and litigation searches below (computed at most once)
        // rather than calling Vertex twice for the same question.
        float[]? queryEmbedding = null;
        async Task<float[]> GetQueryEmbeddingAsync() => queryEmbedding ??= await _embeddingService.EmbedQueryAsync(question, ct);

        var chunkMatches = new List<DocumentChunkMatch>();
        if (authoritativeBatch is { } batch
            && await _db.DocumentChunks.AnyAsync(c => c.RequestId == requestId && c.BatchId == batch.BatchId, ct))
        {
            chunkMatches = await _documentRetriever.SearchRequestDocumentsAsync(requestId, batch.BatchId, await GetQueryEmbeddingAsync(), hints, ct);
        }

        // Litigation has no batch concept — gated only on "at least one litigation chunk already indexed for
        // this request" (mirrors the MCA-filing gate's own reasoning: never call Vertex, or search, before
        // there is anything to find).
        var litigationMatches = new List<LitigationOrderChunkMatch>();
        if (await _db.LitigationOrderChunks.AnyAsync(c => c.RequestId == requestId, ct))
        {
            litigationMatches = await _litigationRetriever.SearchRequestOrdersAsync(requestId, await GetQueryEmbeddingAsync(), ct, hints.LexicalTerms);
        }

        var sources = new List<RetrievedSource>(facts.Count + chunkMatches.Count + litigationMatches.Count);
        var factTag = 1;
        foreach (var f in facts)
            sources.Add(new RetrievedSource($"F{factTag++}", SourceType.StructuredFact, f.Text,
                f.EntityType is null
                    ? $"Computed from parsed data · Ingestion run {f.IngestionRunId} · {f.DomainKey}"
                    : $"Computed from parsed data · Ingestion run {f.IngestionRunId} · {f.EntityType} #{f.EntityId}",
                EntityType: f.EntityType, EntityId: f.EntityId));

        var chunkTag = 1;
        foreach (var m in chunkMatches)
            sources.Add(new RetrievedSource($"D{chunkTag++}", SourceType.DocumentChunk, m.Chunk.ChunkText,
                $"{m.Chunk.DocumentName} · Page {m.Chunk.PageNumber}", RelevanceScore: m.Distance,
                ChunkId: m.Chunk.ChunkId, DocumentName: m.Chunk.DocumentName, PageNumber: m.Chunk.PageNumber,
                DocumentId: m.Chunk.FilingDocumentId));

        var litigationTag = 1;
        foreach (var m in litigationMatches)
        {
            var label = string.IsNullOrWhiteSpace(m.Chunk.CaseNumber)
                ? $"Litigation order · Page {m.Chunk.PageNumber}"
                : $"{m.Chunk.CaseNumber} ({m.Chunk.Court}) · Page {m.Chunk.PageNumber}";
            sources.Add(new RetrievedSource($"L{litigationTag++}", SourceType.LitigationChunk, m.Chunk.ChunkText, label,
                RelevanceScore: m.Distance, ChunkId: m.Chunk.LitigationOrderChunkId, DocumentName: m.Chunk.CaseNumber,
                PageNumber: m.Chunk.PageNumber, DocumentId: m.Chunk.LitigationOrderDocumentId,
                LitigationCaseId: m.Chunk.LitigationCaseId, LitigationCaseOrderId: m.Chunk.LitigationCaseOrderId));
        }

        if (hints.OrderOutcomes is { Count: > 0 } outcomes)
            AddOrderOutcomeSources(sources, outcomes, await _orderOutcomes.FindAsync(requestId, outcomes, ct));

        var indexingStatus = await ComputeIndexingStatusAsync(requestId, ct);
        return new RetrievalContext(sources, indexingStatus);
    }

    /// <summary>Caps the O-sources one prompt carries. The coverage note always states the true match count, so a
    /// capped list is reported as capped rather than passed off as complete.</summary>
    internal const int MaxOrderOutcomeSources = 60;

    /// <summary>#195's structured order-outcome lookup as prompt sources: one "O" source per matching order (exact,
    /// not top-K), then a coverage note — always present, even with zero matches, so "no order with a fine" is only
    /// ever said about orders that were actually classified.</summary>
    internal static void AddOrderOutcomeSources(List<RetrievedSource> sources, IReadOnlyList<LitigationOrderOutcome> asked, OrderOutcomeLookup lookup)
    {
        var tag = 1;
        foreach (var m in lookup.Matches.Take(MaxOrderOutcomeSources))
        {
            var caseLabel = m.CaseNumber ?? m.Cnr ?? "Unnumbered case";
            var label = $"{caseLabel} ({m.Court ?? "court not recorded"}) · Order {m.OrderDate ?? "undated"}";
            var text = new StringBuilder($"Order-outcome classification: order dated {m.OrderDate ?? "undated"}");
            if (!string.IsNullOrWhiteSpace(m.OrderType)) text.Append($" ({m.OrderType})");
            text.Append($" in {caseLabel}, {m.Court ?? "court not recorded"}. Outcomes: {string.Join(", ", m.Outcomes)}.");
            if (m.FineAmount is { } fine) text.Append(CultureInfo.InvariantCulture, $" Fine/penalty stated: Rs {fine:N2}.");
            if (m.Confidence is { } confidence) text.Append($" Classifier confidence: {confidence}.");
            if (m.EvidenceTruncated) text.Append(" Classified from the leading pages only; the order is longer.");
            if (!m.DocumentDownloaded) text.Append(" The order PDF is not currently retained for download.");
            sources.Add(new RetrievedSource($"O{tag++}", SourceType.OrderOutcome, text.ToString(), label,
                EntityType: nameof(LitigationOrderClassification), EntityId: m.LitigationOrderClassificationId,
                DocumentId: m.LitigationOrderDocumentId, LitigationCaseId: m.LitigationCaseId, LitigationCaseOrderId: m.LitigationCaseOrderId));
        }

        var coverage = new StringBuilder($"Order-outcome lookup for {string.Join(", ", asked)}: {lookup.Matches.Count} matching order(s)");
        if (lookup.Matches.Count > MaxOrderOutcomeSources) coverage.Append($" (only the first {MaxOrderOutcomeSources} are listed here)");
        coverage.Append($". Coverage: {lookup.OrdersClassified} of {lookup.OrdersWithText} order(s) with retained text have been classified for their current text.");
        if (lookup.OrdersOutdated > 0)
            coverage.Append($" {lookup.OrdersOutdated} order(s) changed after they were classified; their earlier outcomes are not used until they are re-classified.");
        if (!lookup.IsComplete)
            coverage.Append(" The list is NOT exhaustive: unclassified orders may also have these outcomes (classification runs with the litigation AI analysis).");
        sources.Add(new RetrievedSource($"O{tag}", SourceType.OrderOutcome, coverage.ToString(), "Order-outcome classification coverage",
            EntityType: "OrderOutcomeCoverage"));
    }

    private async Task<string> ComputeIndexingStatusAsync(long requestId, CancellationToken ct)
    {
        var batch = await McaFilings.McaFilingBatchResolver.GetAuthoritativeBatchAsync(_db, requestId, ct);
        if (batch is null)
            return "NotStarted";

        var total = await _db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
            && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed, ct);
        if (total == 0)
            return "NotStarted";

        var chunked = await _db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
            && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
            && d.ChunkingStatus == ChunkingStatus.Chunked, ct);

        return chunked >= total ? "Complete" : $"Partial ({chunked}/{total} documents indexed)";
    }
}
