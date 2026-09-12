using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record RetrievalContext(IReadOnlyList<RetrievedSource> Sources, string IndexingStatusLabel);

/// <summary>ChatService -> RetrievalContextBuilder -> StructuredFactsProvider + DocumentRetriever. This
/// separation is what makes a real question-classifying router easy to slot in later without restructuring
/// ChatService — for now it always combines both sources rather than choosing one.</summary>
public class RetrievalContextBuilder
{
    private readonly AppDbContext _db = null!;
    private readonly StructuredFactsProvider _structuredFacts = null!;
    private readonly DocumentRetriever _documentRetriever = null!;
    private readonly EmbeddingService _embeddingService = null!;

    protected RetrievalContextBuilder() { }

    public RetrievalContextBuilder(
        AppDbContext db, StructuredFactsProvider structuredFacts, DocumentRetriever documentRetriever, EmbeddingService embeddingService)
    {
        _db = db;
        _structuredFacts = structuredFacts;
        _documentRetriever = documentRetriever;
        _embeddingService = embeddingService;
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
        var queryEmbedding = await _embeddingService.EmbedQueryAsync(question, ct);
        var chunkMatches = await _documentRetriever.SearchRequestDocumentsAsync(requestId, authoritativeBatch?.BatchId, queryEmbedding, hints, ct);

        var sources = new List<RetrievedSource>(facts.Count + chunkMatches.Count);
        var factTag = 1;
        foreach (var f in facts)
            sources.Add(new RetrievedSource($"F{factTag++}", SourceType.StructuredFact, f.Text, f.DomainKey,
                EntityType: f.EntityType, EntityId: f.EntityId));

        var chunkTag = 1;
        foreach (var m in chunkMatches)
            sources.Add(new RetrievedSource($"D{chunkTag++}", SourceType.DocumentChunk, m.Chunk.ChunkText,
                $"{m.Chunk.DocumentName} · Page {m.Chunk.PageNumber}", RelevanceScore: m.Distance,
                ChunkId: m.Chunk.ChunkId, DocumentName: m.Chunk.DocumentName, PageNumber: m.Chunk.PageNumber,
                DocumentId: m.Chunk.FilingDocumentId));

        var indexingStatus = await ComputeIndexingStatusAsync(requestId, ct);
        return new RetrievalContext(sources, indexingStatus);
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
