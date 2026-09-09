using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record RetrievalContext(IReadOnlyList<RetrievedSource> Sources, string IndexingStatusLabel);

/// <summary>ChatService -> RetrievalContextBuilder -> StructuredFactsProvider + DocumentRetriever. This
/// separation is what makes a real question-classifying router easy to slot in later without restructuring
/// ChatService — for now it always combines both sources rather than choosing one.</summary>
public class RetrievalContextBuilder(
    AppDbContext db, StructuredFactsProvider structuredFacts, DocumentRetriever documentRetriever, EmbeddingService embeddingService)
{
    public async Task<RetrievalContext> BuildAsync(long requestId, string question, CancellationToken ct)
    {
        var knownLenders = await db.RocCharges.Where(c => c.RequestId == requestId)
            .Select(c => c.LatestChargeHolderNormalized).Distinct().ToListAsync(ct);
        var knownSrns = await db.McaFilings.Where(f => f.RequestId == requestId)
            .Select(f => f.Srn).Distinct().ToListAsync(ct);
        var hints = QuestionHintExtractor.Extract(question, knownLenders, knownSrns);

        var facts = await structuredFacts.BuildDigestAsync(requestId, hints, ct);
        var queryEmbedding = await embeddingService.EmbedQueryAsync(question, ct);
        var chunkMatches = await documentRetriever.SearchRequestDocumentsAsync(requestId, queryEmbedding, hints, ct);

        var sources = new List<RetrievedSource>(facts.Count + chunkMatches.Count);
        var factTag = 1;
        foreach (var f in facts)
            sources.Add(new RetrievedSource($"F{factTag++}", SourceType.StructuredFact, f.Text, f.DomainKey,
                EntityType: f.EntityType, EntityId: f.EntityId));

        var chunkTag = 1;
        foreach (var m in chunkMatches)
            sources.Add(new RetrievedSource($"D{chunkTag++}", SourceType.DocumentChunk, m.Chunk.ChunkText,
                $"{m.Chunk.DocumentName} · Page {m.Chunk.PageNumber}", RelevanceScore: m.Distance,
                ChunkId: m.Chunk.ChunkId, DocumentName: m.Chunk.DocumentName, PageNumber: m.Chunk.PageNumber));

        var indexingStatus = await ComputeIndexingStatusAsync(requestId, ct);
        return new RetrievalContext(sources, indexingStatus);
    }

    private async Task<string> ComputeIndexingStatusAsync(long requestId, CancellationToken ct)
    {
        var batch = await db.McaFilingBatches.Where(b => b.RequestId == requestId)
            .OrderByDescending(b => b.StartedDate).FirstOrDefaultAsync(ct);
        if (batch is null)
            return "NotStarted";

        var total = await db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
            && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed, ct);
        if (total == 0)
            return "NotStarted";

        var chunked = await db.McaFilingDocuments.CountAsync(d =>
            d.BatchId == batch.BatchId && d.DuplicateOfDocumentId == null
            && d.ProcessingStatus == FilingDocumentProcessingStatus.Completed
            && d.ChunkingStatus == ChunkingStatus.Chunked, ct);

        return chunked >= total ? "Complete" : $"Partial ({chunked}/{total} documents indexed)";
    }
}
