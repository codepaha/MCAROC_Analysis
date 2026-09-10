using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Integration tests against the real .\SQLEXPRESS test database — DocumentChunk has no FK to
/// McaRequest, so hand-crafted RequestIds are used directly without seeding a full request graph.
/// Embeddings are hand-crafted unit basis vectors (one axis of a 768-dim vector set to 1) so cosine
/// distances between them are exactly predictable (identical axis = 0, orthogonal axes = ~1, opposite = ~2)
/// without needing a real embedding call.</summary>
public class DocumentRetrieverTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long NextRequestId() => DateTime.UtcNow.Ticks + Random.Shared.Next(0, 1000);

    private static float[] Axis(int dim, float sign = 1f)
    {
        var v = new float[768];
        v[dim] = sign;
        return v;
    }

    private static DocumentChunk NewChunk(long requestId, string srn, FilingCategory category, float[] embedding) => new()
    {
        RequestId = requestId, FilingDocumentId = requestId, FilingId = requestId, BatchId = requestId,
        Srn = srn, Category = category, FormType = null, DocumentName = "doc.pdf",
        ChunkIndex = 0, PageNumber = 1, ChunkText = "chunk text",
        Embedding = new SqlVector<float>(embedding),
        EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
    };

    private static readonly QuestionHints NoHints = new(null, null, null, null, null);

    [Fact]
    public async Task RequestIsolation_NeverReturnsChunksFromAnotherRequest()
    {
        await using var db = CreateContext();
        var requestId = NextRequestId();
        var otherRequestId = NextRequestId() + 1;

        db.DocumentChunks.Add(NewChunk(requestId, "S1", FilingCategory.Charge, Axis(0)));
        db.DocumentChunks.Add(NewChunk(otherRequestId, "S2", FilingCategory.Charge, Axis(0)));
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 2.0 });
        var results = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), NoHints, CancellationToken.None);

        Assert.All(results, r => Assert.Equal(requestId, r.Chunk.RequestId));
        Assert.DoesNotContain(results, r => r.Chunk.RequestId == otherRequestId);
    }

    [Fact]
    public async Task RelevanceThreshold_ExcludesDissimilarChunks()
    {
        await using var db = CreateContext();
        var requestId = NextRequestId();

        db.DocumentChunks.Add(NewChunk(requestId, "S1", FilingCategory.Charge, Axis(0))); // identical to query -> distance 0
        db.DocumentChunks.Add(NewChunk(requestId, "S2", FilingCategory.Charge, Axis(0, -1f))); // opposite -> distance ~2
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 0.5, MinAcceptableResults = 0 });
        var results = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), NoHints, CancellationToken.None);

        var chunk = Assert.Single(results);
        Assert.Equal("S1", chunk.Chunk.Srn);
    }

    [Fact]
    public async Task HardSrnFilter_RestrictsToThatFilingRegardlessOfDistance()
    {
        await using var db = CreateContext();
        var requestId = NextRequestId();

        db.DocumentChunks.Add(NewChunk(requestId, "S1", FilingCategory.Charge, Axis(0)));
        db.DocumentChunks.Add(NewChunk(requestId, "S2", FilingCategory.Charge, Axis(0))); // equally close, different SRN
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 2.0 });
        var hints = new QuestionHints(null, null, null, null, "S1");
        var results = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), hints, CancellationToken.None);

        Assert.All(results, r => Assert.Equal("S1", r.Chunk.Srn));
    }

    [Fact]
    public async Task SoftHintTooNarrow_FailsOpenToUnfilteredSearch()
    {
        // Regression test for the mandatory fail-open behavior: a Category=Charge hint that matches only a
        // far-away chunk must not suppress a much closer chunk in a different category.
        await using var db = CreateContext();
        var requestId = NextRequestId();

        db.DocumentChunks.Add(NewChunk(requestId, "S1", FilingCategory.Compliance, Axis(0))); // close match, wrong category for the hint
        db.DocumentChunks.Add(NewChunk(requestId, "S2", FilingCategory.Charge, Axis(0, -1f))); // hinted category, but far from the query

        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 0.5, MinAcceptableResults = 1 });
        var hints = new QuestionHints(FilingCategory.Charge, null, null, null, null);
        var results = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), hints, CancellationToken.None);

        // The hinted-only search would have returned nothing passing the threshold (S2 is too far) —
        // fail-open must have kicked in and found S1 via the unfiltered fallback.
        var chunk = Assert.Single(results);
        Assert.Equal("S1", chunk.Chunk.Srn);
    }

    [Fact]
    public async Task NoSoftHints_DoesNotRunASecondFallbackQuery()
    {
        // When there's no hint to begin with, the "hinted" search IS the unfiltered search — no redundant
        // second query should change the result set.
        await using var db = CreateContext();
        var requestId = NextRequestId();
        db.DocumentChunks.Add(NewChunk(requestId, "S1", FilingCategory.Charge, Axis(0)));
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, ChatRetrievalOptions.Default with { MaxCosineDistance = 2.0, MinAcceptableResults = 5 });
        var results = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), NoHints, CancellationToken.None);

        Assert.Single(results);
    }
}
