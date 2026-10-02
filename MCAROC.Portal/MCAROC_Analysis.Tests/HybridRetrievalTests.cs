using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#194's test plan against real SQL Server Full-Text Search (no mocks — same discipline as
/// DocumentRetrieverTests): (a) an exact case-number query finds a chunk semantic similarity alone would rank out
/// of the results, (b) fusion doesn't disturb a query with no lexical match, (c) asynchronous full-text
/// population never makes a just-chunked document unfindable. Embeddings are unit basis vectors, as in
/// DocumentRetrieverTests, so every cosine distance is exactly predictable.</summary>
public class HybridRetrievalTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long NextRequestId() => DateTime.UtcNow.Ticks + Random.Shared.Next(0, 1000);

    private static float[] Axis(int dim)
    {
        var v = new float[768];
        v[dim] = 1f;
        return v;
    }

    private static DocumentChunk NewChunk(long requestId, string srn, string text, float[] embedding) => new()
    {
        RequestId = requestId, FilingDocumentId = -requestId, FilingId = requestId, BatchId = requestId, // negative: never a real document id
        Srn = srn, Category = FilingCategory.Unclassified, FormType = null, DocumentName = $"{srn}.pdf",
        ChunkIndex = 0, PageNumber = 1, ChunkText = text,
        Embedding = new SqlVector<float>(embedding),
        EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
    };

    private static QuestionHints Lexical(params string[] terms) => new(null, null, null, null, null, terms);

    // TopK 2 with two semantically perfect filler chunks: the orthogonal case-number chunk (distance 1) is both
    // past the threshold and outside the top-K, so only the lexical path can surface it.
    private static readonly ChatRetrievalOptions Options =
        ChatRetrievalOptions.Default with { TopK = 2, MaxCosineDistance = 0.5, MinAcceptableResults = 0 };

    private static async Task<(long RequestId, DocumentChunk Target)> SeedCaseNumberCorpusAsync(AppDbContext db)
    {
        var requestId = NextRequestId();
        db.DocumentChunks.Add(NewChunk(requestId, "F1", "General narrative about the company's lenders and its charges.", Axis(0)));
        db.DocumentChunks.Add(NewChunk(requestId, "F2", "Another general passage on the borrower's security package.", Axis(0)));
        var target = NewChunk(requestId, "T1", "Order in TP 255/2019 dated 10-02-2022: the petition is admitted.", Axis(1));
        db.DocumentChunks.Add(target);
        await db.SaveChangesAsync();
        return (requestId, target);
    }

    [SkippableFact]
    public async Task ExactCaseNumber_IsFound_EvenWhenSemanticSearchAloneWouldMissIt()
    {
        await FullTextTestSupport.RequireFullTextIndexAsync("dbo.DocumentChunks");
        await using var db = CreateContext();
        var (requestId, target) = await SeedCaseNumberCorpusAsync(db);
        await FullTextTestSupport.WaitUntilIndexedAsync("DocumentChunks", "ChunkId", target.ChunkId, "\"TP 255/2019\"");

        var retriever = new DocumentRetriever(db, Options);

        var semanticOnly = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical(), CancellationToken.None);
        Assert.DoesNotContain(semanticOnly, m => m.Chunk.ChunkId == target.ChunkId);

        var hybrid = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical("TP 255/2019"), CancellationToken.None);
        Assert.Contains(hybrid, m => m.Chunk.ChunkId == target.ChunkId);
        Assert.Equal(Options.TopK, hybrid.Count);
        Assert.Equal(1.0, hybrid.Single(m => m.Chunk.ChunkId == target.ChunkId).Distance, 3); // real cosine distance kept
    }

    [SkippableFact]
    public async Task LexicalTermWithNoMatch_LeavesTheSemanticResultUnchanged()
    {
        await FullTextTestSupport.RequireFullTextIndexAsync("dbo.DocumentChunks");
        await using var db = CreateContext();
        var (requestId, target) = await SeedCaseNumberCorpusAsync(db);
        await FullTextTestSupport.WaitUntilIndexedAsync("DocumentChunks", "ChunkId", target.ChunkId, "\"TP 255/2019\"");

        var retriever = new DocumentRetriever(db, Options);
        var semanticOnly = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical(), CancellationToken.None);
        var hybrid = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical("WP 99999/2001"), CancellationToken.None);

        Assert.Equal(semanticOnly.Select(m => m.Chunk.ChunkId), hybrid.Select(m => m.Chunk.ChunkId));
    }

    [SkippableFact]
    public async Task LexicalSearch_NeverCrossesTheRequestBoundary()
    {
        await FullTextTestSupport.RequireFullTextIndexAsync("dbo.DocumentChunks");
        await using var db = CreateContext();
        var (requestId, _) = await SeedCaseNumberCorpusAsync(db);
        var other = NewChunk(NextRequestId() + 1, "X1", "Other borrower's order in WP 11227/2019 — confidential.", Axis(1));
        db.DocumentChunks.Add(other);
        await db.SaveChangesAsync();
        await FullTextTestSupport.WaitUntilIndexedAsync("DocumentChunks", "ChunkId", other.ChunkId, "\"WP 11227/2019\"");

        var retriever = new DocumentRetriever(db, Options);
        var hybrid = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical("WP 11227/2019"), CancellationToken.None);

        Assert.All(hybrid, m => Assert.Equal(requestId, m.Chunk.RequestId));
    }

    [SkippableFact]
    public async Task JustChunkedDocument_IsFoundSemanticallyAtOnce_AndLexicallyOncePopulated()
    {
        await FullTextTestSupport.RequireFullTextIndexAsync("dbo.DocumentChunks");
        await using var db = CreateContext();
        var requestId = NextRequestId();
        var chunk = NewChunk(requestId, "N1", "Fresh order in IA 45/2021 allowing the application.", Axis(0));
        db.DocumentChunks.Add(chunk);
        await db.SaveChangesAsync();

        var retriever = new DocumentRetriever(db, Options);

        // Immediately after insert — full-text population may not have caught up yet, but the vector path sees
        // the row at once and the lexical path can only add, so the chunk is found either way (no flaky window).
        var immediate = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical("IA 45/2021"), CancellationToken.None);
        Assert.Contains(immediate, m => m.Chunk.ChunkId == chunk.ChunkId);

        await FullTextTestSupport.WaitUntilIndexedAsync("DocumentChunks", "ChunkId", chunk.ChunkId, "\"IA 45/2021\"");
        var populated = await retriever.SearchRequestDocumentsAsync(requestId, Axis(0), Lexical("IA 45/2021"), CancellationToken.None);
        Assert.Single(populated, m => m.Chunk.ChunkId == chunk.ChunkId); // fused, never duplicated
    }
}
