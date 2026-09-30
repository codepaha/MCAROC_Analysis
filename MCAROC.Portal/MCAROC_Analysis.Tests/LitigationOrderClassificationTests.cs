using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Epic #195 end to end against real SQL Server: a litigation AI analysis run classifies every order with
/// retained text (inside the run's existing admission), carries unchanged orders forward without another model call,
/// persists rejected model output as Failed, and <see cref="LitigationOrderOutcomeQuery"/> answers "every order with
/// outcome X" exactly — with coverage reported, and never across requests. The model is a scripted fake.</summary>
public sealed class LitigationOrderClassificationTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string FineOrderText = "Heard counsel. The respondent shall pay costs of Rs. 50,000 within four weeks.";
    private const string DismissalOrderText = "The company petition is dismissed for non-prosecution.";

    /// <summary>Answers each of the run's three prompt kinds with valid, evidence-citing JSON built from the prompt
    /// itself, and lets a test script the order-classification answer.</summary>
    private sealed class ScriptedClient(Func<string, string> classify) : ILitigationAiAnalysisClient
    {
        public int ClassificationCalls;

        public Task<LitigationAiCallResult> CallAsync(string prompt, int timeoutSeconds, CancellationToken ct)
        {
            if (prompt.StartsWith(LitigationOrderClassifier.PromptMarker, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref ClassificationCalls);
                return Ok(classify(prompt));
            }
            if (prompt.Contains("portfolio-level", StringComparison.Ordinal))
            {
                var ids = Regex.Matches(prompt, "\"LitigationCaseAiAnalysisId\":(\\d+)").Select(m => m.Groups[1].Value);
                return Ok($$"""{"status":"Completed","summary":"Portfolio.","unknowns":[],"caseAnalysisIds":[{{string.Join(",", ids)}}]}""");
            }
            // Only after "Evidence:" — the prompt's own response-shape example also contains a reference.
            var evidence = prompt[prompt.IndexOf("Evidence:", StringComparison.Ordinal)..];
            var r = Regex.Match(evidence, "\"litigationCaseOrderId\":(\\d+),\"litigationOrderDocumentId\":(\\d+),\"pageNumber\":(\\d+),\"chunkIndex\":(\\d+)");
            return Ok($$"""{"status":"Completed","summary":"Case.","unknowns":[],"evidenceReferences":[{"litigationCaseOrderId":{{r.Groups[1].Value}},"litigationOrderDocumentId":{{r.Groups[2].Value}},"pageNumber":{{r.Groups[3].Value}},"chunkIndex":{{r.Groups[4].Value}}}]}""");
        }

        private static Task<LitigationAiCallResult> Ok(string json) => Task.FromResult(new LitigationAiCallResult(true, json, null));
    }

    /// <summary>Classifies by the order's own text, citing its only excerpt (page 1, chunk 0).</summary>
    private static string ClassifyByText(string prompt) => prompt.Contains("costs of Rs. 50,000")
        ? """{"status":"Completed","confidence":"High","outcomes":[{"type":"FinePenalty","fineAmount":50000,"evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}"""
        : """{"status":"Completed","confidence":"Medium","outcomes":[{"type":"Dismissal","evidenceReferences":[{"pageNumber":1,"chunkIndex":0}]}]}""";

    private static LitigationAiAnalysisOrchestrator Orchestrator(AppDbContext db, ILitigationAiAnalysisClient client) =>
        new(db, client, new LitigationAiAnalysisQueue(), Options.Create(new LitigationAiAnalysisOptions()), NullLogger<LitigationAiAnalysisOrchestrator>.Instance);

    private static async Task<LitigationAiAnalysisRun> RunAnalysisAsync(long requestId, ILitigationAiAnalysisClient client)
    {
        await using var db = CreateContext();
        var orchestrator = Orchestrator(db, client);
        var run = await orchestrator.CreateOrJoinAsync(requestId, LitigationAiAnalysisTrigger.Manual, null, null, CancellationToken.None);
        await orchestrator.RunAsync(run.LitigationAiAnalysisRunId, CancellationToken.None);
        await using var verify = CreateContext();
        return await verify.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId);
    }

    [Fact]
    public async Task A_run_classifies_every_order_and_the_lookup_returns_exactly_the_matching_ones()
    {
        var (requestId, fineDocId, _) = await SeedRequestWithOrdersAsync();
        var otherRequest = await SeedRequestWithOrdersAsync();
        var client = new ScriptedClient(ClassifyByText);

        var run = await RunAnalysisAsync(requestId, client);

        Assert.Equal(LitigationAiAnalysisRunStatus.Completed, run.Status);
        Assert.Equal(2, client.ClassificationCalls);
        await using var db = CreateContext();
        var rows = await db.LitigationOrderClassifications.AsNoTracking().Where(c => c.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(LitigationAiAnalysisItemStatus.Completed, r.Status));

        var lookup = await new LitigationOrderOutcomeQuery(db).FindAsync(requestId, [LitigationOrderOutcome.FinePenalty], CancellationToken.None);
        var match = Assert.Single(lookup.Matches);
        Assert.Equal(fineDocId, match.LitigationOrderDocumentId);
        Assert.Equal([LitigationOrderOutcome.FinePenalty], match.Outcomes);
        Assert.Equal(50000m, match.FineAmount);
        Assert.Equal(ClassificationConfidence.High, match.Confidence);
        Assert.Equal("CP 12/2020", match.CaseNumber);
        Assert.True(match.DocumentDownloaded);
        Assert.Equal((2, 2), (lookup.OrdersClassified, lookup.OrdersWithText));
        Assert.True(lookup.IsComplete);

        // The other request's identical orders were never classified, and nothing of this request's leaks into it.
        var other = await new LitigationOrderOutcomeQuery(db).FindAsync(otherRequest.RequestId, [LitigationOrderOutcome.FinePenalty], CancellationToken.None);
        Assert.Empty(other.Matches);
        Assert.Equal((0, 2), (other.OrdersClassified, other.OrdersWithText));
        Assert.False(other.IsComplete);

        Assert.False(await Orchestrator(db, client).NeedsAnalysisAsync(requestId, CancellationToken.None));
    }

    [Fact]
    public async Task A_second_run_carries_unchanged_orders_forward_without_another_model_call()
    {
        var (requestId, _, _) = await SeedRequestWithOrdersAsync();
        var client = new ScriptedClient(ClassifyByText);
        await RunAnalysisAsync(requestId, client);
        Assert.Equal(2, client.ClassificationCalls);

        var second = await RunAnalysisAsync(requestId, client);

        Assert.Equal(2, client.ClassificationCalls); // no new paid call for either order
        await using var db = CreateContext();
        var carried = await db.LitigationOrderClassifications.AsNoTracking()
            .Where(c => c.LitigationAiAnalysisRunId == second.LitigationAiAnalysisRunId).ToListAsync();
        Assert.Equal(2, carried.Count);
        Assert.Contains(carried, c => c.OutcomeTypesJson == "[\"FinePenalty\"]" && c.FineAmount == 50000m);
    }

    [Fact]
    public async Task Output_citing_evidence_that_was_never_sent_is_persisted_as_Failed_and_never_matches()
    {
        var (requestId, _, _) = await SeedRequestWithOrdersAsync();
        var client = new ScriptedClient(_ =>
            """{"status":"Completed","outcomes":[{"type":"FinePenalty","fineAmount":1,"evidenceReferences":[{"pageNumber":9,"chunkIndex":0}]}]}""");

        var run = await RunAnalysisAsync(requestId, client);

        Assert.Equal(LitigationAiAnalysisRunStatus.CompletedWithErrors, run.Status);
        await using var db = CreateContext();
        var rows = await db.LitigationOrderClassifications.AsNoTracking().Where(c => c.LitigationAiAnalysisRunId == run.LitigationAiAnalysisRunId).ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.Equal(LitigationAiAnalysisItemStatus.Failed, r.Status);
            Assert.Contains("not present in the prompt", r.FailureReason);
            Assert.Equal("[]", r.OutcomeTypesJson);
            Assert.NotNull(r.RawResponseJson); // the rejected answer is kept for audit
        });

        var lookup = await new LitigationOrderOutcomeQuery(db).FindAsync(requestId, [LitigationOrderOutcome.FinePenalty], CancellationToken.None);
        Assert.Empty(lookup.Matches);
        Assert.Equal(0, lookup.OrdersClassified);
        Assert.True(await Orchestrator(db, client).NeedsAnalysisAsync(requestId, CancellationToken.None)); // failed orders still need work
    }

    [Fact]
    public async Task A_later_failed_classification_never_displaces_an_earlier_good_one()
    {
        var (requestId, fineDocId, _) = await SeedRequestWithOrdersAsync();
        var firstRun = await RunAnalysisAsync(requestId, new ScriptedClient(ClassifyByText));

        await using (var db = CreateContext())
        {
            var good = await db.LitigationOrderClassifications.AsNoTracking()
                .SingleAsync(c => c.LitigationAiAnalysisRunId == firstRun.LitigationAiAnalysisRunId && c.LitigationOrderDocumentId == fineDocId);
            var laterRun = new LitigationAiAnalysisRun
            {
                RequestId = requestId, RunNumber = firstRun.RunNumber + 1, Status = LitigationAiAnalysisRunStatus.CompletedWithErrors,
                ModelId = "m", PromptVersion = "1.0", CreatedUtc = DateTime.UtcNow, CompletedUtc = DateTime.UtcNow
            };
            db.LitigationAiAnalysisRuns.Add(laterRun);
            await db.SaveChangesAsync();
            db.LitigationOrderClassifications.Add(new LitigationOrderClassification
            {
                LitigationAiAnalysisRunId = laterRun.LitigationAiAnalysisRunId, RequestId = requestId, LitigationCaseId = good.LitigationCaseId,
                LitigationCaseOrderId = good.LitigationCaseOrderId, LitigationOrderDocumentId = fineDocId,
                Status = LitigationAiAnalysisItemStatus.Failed, EvidenceJson = "{}", EvidenceHash = "h", PromptHash = "p",
                FailureReason = "timed out", CompletedUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await using var verify = CreateContext();
        var lookup = await new LitigationOrderOutcomeQuery(verify).FindAsync(requestId, [LitigationOrderOutcome.FinePenalty], CancellationToken.None);
        Assert.Equal(fineDocId, Assert.Single(lookup.Matches).LitigationOrderDocumentId);
        Assert.True(lookup.IsComplete);
    }

    [Fact]
    public async Task An_order_that_gains_text_after_a_run_makes_the_request_need_analysis_again()
    {
        var (requestId, _, caseId) = await SeedRequestWithOrdersAsync();
        await RunAnalysisAsync(requestId, new ScriptedClient(ClassifyByText));

        await using var db = CreateContext();
        Assert.False(await Orchestrator(db, null!).NeedsAnalysisAsync(requestId, CancellationToken.None));

        await SeedOrderAsync(db, requestId, caseId, "The stay granted earlier is vacated.");

        Assert.True(await Orchestrator(db, null!).NeedsAnalysisAsync(requestId, CancellationToken.None));
    }

    private static async Task<(long RequestId, long FineDocId, long CaseId)> SeedRequestWithOrdersAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "LOC" + Guid.NewGuid().ToString("N")[..7], ClientName = "Outcome Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Outcome Test Co",
            Cin = $"U{Random.Shared.Next(10000, 99999)}TN2001PLC{Random.Shared.Next(100000, 999999)}",
            RequestNumber = $"LOC-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var litigationCase = new LitigationCase
        {
            RequestId = request.RequestId, Cnr = "CNR" + Guid.NewGuid().ToString("N")[..10], CaseNumber = "CP 12/2020",
            Court = "NCLT Chennai", ProceedingType = "CP", FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(litigationCase);
        await db.SaveChangesAsync();

        var fineDocId = await SeedOrderAsync(db, request.RequestId, litigationCase.LitigationCaseId, FineOrderText);
        await SeedOrderAsync(db, request.RequestId, litigationCase.LitigationCaseId, DismissalOrderText);
        return (request.RequestId, fineDocId, litigationCase.LitigationCaseId);
    }

    private static async Task<long> SeedOrderAsync(AppDbContext db, long requestId, long caseId, string text)
    {
        var order = new LitigationCaseOrder { LitigationCaseId = caseId, OrderDate = "10-02-2022", OrderType = "Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();
        var document = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5), ExtractedText = text, CreatedUtc = DateTime.UtcNow
        };
        db.LitigationOrderDocuments.Add(document);
        await db.SaveChangesAsync();
        db.LitigationOrderChunks.Add(new LitigationOrderChunk
        {
            RequestId = requestId, LitigationOrderDocumentId = document.LitigationOrderDocumentId, LitigationCaseOrderId = order.LitigationCaseOrderId,
            LitigationCaseId = caseId, CaseNumber = "CP 12/2020", Court = "NCLT Chennai", OrderDate = order.OrderDate, OrderType = order.OrderType,
            ChunkIndex = 0, PageNumber = 1, ChunkText = text, Embedding = new SqlVector<float>(new float[768]),
            EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return document.LitigationOrderDocumentId;
    }
}
