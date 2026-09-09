using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Chat;

public record DocumentChunkMatch(DocumentChunk Chunk, double Distance);

/// <summary>Exposes exactly one public entry point — SearchRequestDocumentsAsync — never a raw
/// SearchAsync(queryVector) that would trust a caller to remember the RequestId filter. Makes cross-request
/// leakage structurally harder, not just procedurally avoided.</summary>
public class DocumentRetriever(AppDbContext db, ChatRetrievalOptions options)
{
    public async Task<List<DocumentChunkMatch>> SearchRequestDocumentsAsync(
        long requestId, float[] queryEmbedding, QuestionHints hints, CancellationToken ct)
    {
        var queryVector = new SqlVector<float>(queryEmbedding);

        var hinted = await SearchAsync(requestId, queryVector, hints, ct);
        var passingHinted = hinted.Where(m => m.Distance <= options.MaxCosineDistance).ToList();

        if (!hints.HasSoftHints || passingHinted.Count >= options.MinAcceptableResults)
            return passingHinted;

        // Fail open: the soft-hinted search didn't yield enough evidence — retry with only the RequestId
        // (and any hard SRN match) filter, so an overly-specific hint can never silently starve the answer
        // of evidence an unfiltered search would have found.
        var fallbackHints = hints with { Category = null, FormTypeKeyword = null, LenderNameKeyword = null };
        var fallback = await SearchAsync(requestId, queryVector, fallbackHints, ct);
        return fallback.Where(m => m.Distance <= options.MaxCosineDistance).ToList();
    }

    private async Task<List<DocumentChunkMatch>> SearchAsync(long requestId, SqlVector<float> queryVector, QuestionHints hints, CancellationToken ct)
    {
        var query = db.DocumentChunks.Where(c => c.RequestId == requestId);
        if (hints.SrnMatch is not null) query = query.Where(c => c.Srn == hints.SrnMatch);
        if (hints.Category is not null) query = query.Where(c => c.Category == hints.Category);
        if (hints.FormTypeKeyword is not null) query = query.Where(c => c.FormType != null && c.FormType.Contains(hints.FormTypeKeyword));
        if (hints.LenderNameKeyword is not null) query = query.Where(c => c.ChunkText.Contains(hints.LenderNameKeyword));

        var rows = await query
            .OrderBy(c => EF.Functions.VectorDistance("cosine", c.Embedding, queryVector))
            .Take(options.TopK)
            .Select(c => new { Chunk = c, Distance = EF.Functions.VectorDistance("cosine", c.Embedding, queryVector) })
            .ToListAsync(ct);

        return rows.Select(r => new DocumentChunkMatch(r.Chunk, r.Distance)).ToList();
    }
}
