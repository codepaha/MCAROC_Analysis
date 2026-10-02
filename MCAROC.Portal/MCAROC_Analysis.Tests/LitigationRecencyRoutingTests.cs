using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#343 through the real RetrievalContextBuilder.BuildAsync on the .\SQLEXPRESS test database. Every chunk
/// has the same embedding, so similarity cannot tell orders apart — exactly the real failure, where "current status
/// of TP 255/2019" was answered from a 2023 order. The case record and the newest orders by date must come from the
/// structured route instead; identical passages filed under two cases must appear once.</summary>
public class LitigationRecencyRoutingTests : IAsyncLifetime
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

    private static RetrievalContextBuilder NewBuilder(AppDbContext db) => new(
        db, new StructuredFactsProvider(db), new DocumentRetriever(db, ChatRetrievalOptions.Default),
        new LitigationDocumentRetriever(db, ChatRetrievalOptions.Default), new FixedEmbeddingService(), new LitigationOrderOutcomeQuery(db));

    private static async Task<long> SeedRequestAsync(AppDbContext db)
    {
        var request = new McaRequest
        {
            ClientId = 1, EntityType = EntityType.Company, CompanyName = "Recency Co",
            RequestNumber = $"LRR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.AnalysisCompleted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    private static async Task<LitigationCase> SeedCaseAsync(AppDbContext db, long requestId, string number, params (string Date, string Text)[] orders)
    {
        var c = new LitigationCase
        {
            RequestId = requestId, Cnr = "CNR" + Guid.NewGuid().ToString("N")[..10], CaseNumber = number, Court = "NCLT Cuttack",
            ProceedingType = "TP", CaseStatus = "Pending", NextHearingDate = "24-08-2026",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow,
            Orders = orders.Select(o => new LitigationCaseOrder { OrderDate = o.Date, OrderType = "Order", CreatedUtc = DateTime.UtcNow }).ToList()
        };
        db.LitigationCases.Add(c);
        await db.SaveChangesAsync();

        for (var i = 0; i < orders.Length; i++)
            db.LitigationOrderChunks.Add(new LitigationOrderChunk
            {
                RequestId = requestId, LitigationOrderDocumentId = c.Orders[i].LitigationCaseOrderId, LitigationCaseOrderId = c.Orders[i].LitigationCaseOrderId,
                LitigationCaseId = c.LitigationCaseId, CaseNumber = number, Court = "NCLT Cuttack", OrderType = "Order", OrderDate = orders[i].Date,
                ChunkIndex = 0, PageNumber = 1, ChunkText = orders[i].Text, Embedding = new SqlVector<float>(Axis0()),
                EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
            });
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task CurrentStatusOfANamedCase_GetsItsRecord_AndItsNewestOrdersFirst()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        var tp = await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019",
            ("12-09-2023", "Interim order extended; listed 17.10.2023."),
            ("20-07-2026", "31st progress report taken on record; list on 24.08.2026."),
            ("15-03-2019", "Liquidation order passed."),
            ("06-05-2026", "Pending IAs listed with the main CP."),
            ("01-01-2025", "Matter adjourned."));
        await SeedCaseAsync(db, requestId, "TP 93/CTB/2019", ("25-03-2019", "Issue notice to the petitioner."));

        var context = await NewBuilder(db).BuildAsync(requestId, "What is the current status of TP 255/2019?", CancellationToken.None);

        var record = Assert.Single(context.Sources, s => s.Type == SourceType.LitigationCase);
        Assert.Equal(tp.LitigationCaseId, record.LitigationCaseId);
        Assert.Contains("Next hearing: 24-08-2026.", record.Text);
        Assert.Contains("newest first: 20-07-2026", record.Text);

        var litigation = context.Sources.Where(s => s.Type == SourceType.LitigationChunk).ToList();
        Assert.Equal(["Order 20-07-2026", "Order 06-05-2026", "Order 01-01-2025"],
            litigation.Take(3).Select(s => s.DisplayLabel.Split(" · ")[1]));
        Assert.Equal("L1", litigation[0].Tag);
        Assert.Null(litigation[0].RelevanceScore);
    }

    [Fact]
    public async Task NamedCaseWithoutRecencyCue_GetsItsRecord_ButNoDateOrderedOrders()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019", ("12-09-2023", "Interim order extended."), ("20-07-2026", "Progress report."));

        var context = await NewBuilder(db).BuildAsync(requestId, "What is TP 255/2019 about?", CancellationToken.None);

        Assert.Single(context.Sources, s => s.Type == SourceType.LitigationCase);
        Assert.All(context.Sources.Where(s => s.Type == SourceType.LitigationChunk), s => Assert.NotNull(s.RelevanceScore));
    }

    [Fact]
    public async Task UnknownCaseNumber_AddsNoCaseRecord()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019", ("12-09-2023", "Interim order extended."));

        var context = await NewBuilder(db).BuildAsync(requestId, "What is the current status of TP 999/2019?", CancellationToken.None);

        Assert.DoesNotContain(context.Sources, s => s.Type == SourceType.LitigationCase);
    }

    [Fact]
    public async Task IdenticalOrderFiledUnderTwoCases_IsRetrievedOnce()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        const string shared = "Issue notice. Reply within three weeks. List this appeal on 21.09.2023.";
        await SeedCaseAsync(db, requestId, "Company Appeal (AT)(Ins) 935/2023", ("01-08-2023", shared), ("21-09-2023", "Heard counsel."));
        await SeedCaseAsync(db, requestId, "IA 3183/2023", ("01-08-2023", shared));

        var context = await NewBuilder(db).BuildAsync(requestId, "What did the tribunal direct about the reply?", CancellationToken.None);

        var texts = context.Sources.Where(s => s.Type == SourceType.LitigationChunk).Select(s => s.Text).ToList();
        Assert.Equal(2, texts.Count);
        Assert.Single(texts, t => t == shared);
    }

    // ── #353: "what was decided" gets the named case's decision passages ahead of similarity matches ──

    private const string Recital = "Heard the learned counsel for the parties and perused the record placed before the Tribunal.";
    private const string Decision = "In the result, the application is dismissed. The applicant shall pay costs of Rs. 5,000. Ordered accordingly.";

    [Fact]
    public async Task DecisionQuestionAboutANamedCase_PinsItsDecisionPassage_First()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019",
            ("12-09-2023", Recital), ("15-03-2021", Decision), ("20-07-2026", Recital + " Matter listed for further hearing."));
        await SeedCaseAsync(db, requestId, "TP 93/CTB/2019", ("25-03-2019", "The petition is allowed and the stay is vacated hereby."));

        var context = await NewBuilder(db).BuildAsync(requestId, "What was decided in TP 255/2019?", CancellationToken.None);

        var litigation = context.Sources.Where(s => s.Type == SourceType.LitigationChunk).ToList();
        Assert.Equal(Decision, litigation[0].Text);
        Assert.Null(litigation[0].RelevanceScore); // pinned, not a similarity match
        // Only the named case's decision passages are pinned; recitals (no decision language) never are.
        Assert.Single(litigation, s => s.RelevanceScore is null);
        Assert.Single(litigation, s => s.Text == Decision); // and not repeated by the similarity matches
    }

    [Fact]
    public async Task DecisionAndRecencyQuestion_PinsNewestOrdersThenDecisionPassages_WithoutRepeats()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019",
            ("20-07-2026", Recital), ("06-05-2026", Recital + " Adjourned."), ("01-01-2025", Recital + " Notice issued."),
            ("15-03-2021", Decision));

        var context = await NewBuilder(db).BuildAsync(requestId, "What was the latest decision in TP 255/2019?", CancellationToken.None);

        var pinned = context.Sources.Where(s => s.Type == SourceType.LitigationChunk && s.RelevanceScore is null).ToList();
        Assert.Equal(["Order 20-07-2026", "Order 06-05-2026", "Order 01-01-2025", "Order 15-03-2021"],
            pinned.Select(s => s.DisplayLabel.Split(" · ")[1]));
        Assert.Equal(pinned.Count, pinned.Select(s => s.ChunkId).Distinct().Count());
    }

    [Fact]
    public async Task DecisionQuestionWithoutANamedCase_PinsNothing()
    {
        await using var db = CreateContext();
        var requestId = await SeedRequestAsync(db);
        await SeedCaseAsync(db, requestId, "TP(IB) 255/CTB/2019", ("15-03-2021", Decision));

        var context = await NewBuilder(db).BuildAsync(requestId, "Which petitions were dismissed?", CancellationToken.None);

        Assert.All(context.Sources.Where(s => s.Type == SourceType.LitigationChunk), s => Assert.NotNull(s.RelevanceScore));
    }
}
