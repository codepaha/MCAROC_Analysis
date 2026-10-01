using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#338 review: the S1 search-coverage note through the real RetrievalContextBuilder.BuildAsync against
/// the .\SQLEXPRESS test database — every count or list-all wording that is answered from retrieved litigation
/// passages carries S1, other questions do not, and no S1 is added when there are no passages to rely on.</summary>
public class SearchCoverageRoutingTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static float[] Axis0()
    {
        var v = new float[768];
        v[0] = 1f;
        return v;
    }

    private sealed class FixedEmbeddingService : EmbeddingService
    {
        public override Task<float[]> EmbedQueryAsync(string text, CancellationToken ct) => Task.FromResult(Axis0());
    }

    private static async Task<long> SeedRequestAsync(AppDbContext db, int litigationChunks)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Coverage Co",
            RequestNumber = $"SCR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        for (var i = 0; i < litigationChunks; i++)
            db.LitigationOrderChunks.Add(new LitigationOrderChunk
            {
                RequestId = request.RequestId, LitigationOrderDocumentId = i + 1, LitigationCaseOrderId = i + 1, LitigationCaseId = i + 1,
                CaseNumber = $"CS {i + 1}/2021", Court = "High Court", ChunkIndex = 0, PageNumber = 1,
                ChunkText = $"Order {i + 1}: the plaintiff alleges fraud in the loan documents.",
                Embedding = new SqlVector<float>(Axis0()),
                EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    private static RetrievalContextBuilder NewBuilder(AppDbContext db) => new(
        db, new StructuredFactsProvider(db), new DocumentRetriever(db, ChatRetrievalOptions.Default),
        new LitigationDocumentRetriever(db, ChatRetrievalOptions.Default), new FixedEmbeddingService(), new LitigationOrderOutcomeQuery(db));

    [Theory]
    [InlineData("How many cases mention fraud?")]
    [InlineData("Count the cases mentioning fraud.")]
    [InlineData("What is the number of cases mentioning fraud?")]
    [InlineData("What is the total number of cases mentioning fraud?")]
    [InlineData("List all cases mentioning fraud.")]
    public async Task CountAndListQuestions_AnsweredFromPassages_CarryTheSearchCoverageNote(string question)
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db, litigationChunks: 3);

        var context = await NewBuilder(db).BuildAsync(requestId, question, CancellationToken.None);

        Assert.Contains(context.Sources, s => s.Type == SourceType.LitigationChunk);
        var note = Assert.Single(context.Sources, s => s.Type == SourceType.SearchCoverage);
        Assert.Equal("S1", note.Tag);
        Assert.Contains("of 3 indexed litigation-order passage(s)", note.Text);
        Assert.Contains("NOT exhaustive", note.Text);
    }

    [Theory]
    [InlineData("Who is the auditor?")]
    [InlineData("Summarize the order on fraud.")]
    public async Task OtherQuestions_GetNoSearchCoverageNote(string question)
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db, litigationChunks: 3);

        var context = await NewBuilder(db).BuildAsync(requestId, question, CancellationToken.None);

        Assert.Contains(context.Sources, s => s.Type == SourceType.LitigationChunk);
        Assert.DoesNotContain(context.Sources, s => s.Type == SourceType.SearchCoverage);
    }

    [Fact]
    public async Task CountQuestion_WithNoPassagesToRelyOn_GetsNoSearchCoverageNote()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db, litigationChunks: 0);

        var context = await NewBuilder(db).BuildAsync(requestId, "Count the cases mentioning fraud.", CancellationToken.None);

        Assert.DoesNotContain(context.Sources, s => s.Type == SourceType.SearchCoverage);
    }
}
