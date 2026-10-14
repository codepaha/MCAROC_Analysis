using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#499: answered questions are masked, embedded once and grouped by meaning across clients; frequent well-answered groups become suggestions.</summary>
public class QuestionRepositoryServiceTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Embeds by topic: a text containing a key gets that key's direction plus a little noise, anything else a random direction (so questions from
    /// other tests in the shared database never land near these).</summary>
    private sealed class TopicEmbeddings(Dictionary<string, float[]> topics) : EmbeddingService
    {
        public List<string> Embedded { get; } = [];

        public override Task<List<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            Embedded.AddRange(texts);
            return Task.FromResult(texts.Select(t =>
            {
                var key = topics.Keys.FirstOrDefault(k => t.Contains(k, StringComparison.OrdinalIgnoreCase));
                var v = key is null ? RandomUnit() : topics[key].Zip(RandomUnit(), (a, n) => a + 0.05f * n).ToArray();
                return Unit(v);
            }).ToList());
        }
    }

    private static float[] RandomUnit() => Unit(Enumerable.Range(0, EmbeddingService.Dimensions).Select(_ => (float)(Random.Shared.NextDouble() * 2 - 1)).ToArray());

    private static float[] Unit(float[] v)
    {
        var n = (float)Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => x / n).ToArray();
    }

    private static QuestionRepositoryService Service(AppDbContext db, EmbeddingService e, int maxPerDay = int.MaxValue) =>
        new(db, e, Options.Create(new QuestionRepositoryOptions { MaxQuestionsPerDay = maxPerDay }), TimeProvider.System);

    private static async Task<long> RequestAsync(AppDbContext db, string company, string? director = null)
    {
        var client = new Client { ClientCode = "QR" + Guid.NewGuid().ToString("N")[..8], ClientName = "Question repo", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest { Client = client, EntityType = EntityType.Company, CompanyName = company, RequestNumber = $"QR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        if (director is not null)
        {
            var run = new IngestionRun { RequestId = request.RequestId, RunNumber = 1, StartedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync();
            db.Directors.Add(new Director { RequestId = request.RequestId, IngestionRunId = run.IngestionRunId, NameRaw = director, NameNormalized = director.ToUpperInvariant() });
            await db.SaveChangesAsync();
        }
        return request.RequestId;
    }

    /// <summary>A user question and, unless <paramref name="reply"/> is null, the assistant's reply.</summary>
    private static async Task<long> AskAsync(AppDbContext db, long requestId, string question, ChatMessageStatus? reply = ChatMessageStatus.Success,
        string? cited = "[{\"sourceType\":\"StructuredFact\"}]", string? model = "gemini")
    {
        // A request has one chat session.
        var session = await db.ChatSessions.FirstOrDefaultAsync(x => x.RequestId == requestId);
        if (session is null)
        {
            session = new ChatSession { RequestId = requestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow };
            db.ChatSessions.Add(session);
            await db.SaveChangesAsync();
        }
        var q = new ChatMessage { ChatSessionId = session.ChatSessionId, Role = ChatRole.User, MessageText = question, CreatedDate = DateTime.UtcNow };
        db.ChatMessages.Add(q);
        await db.SaveChangesAsync();
        if (reply is not null)
        {
            db.ChatMessages.Add(new ChatMessage { ChatSessionId = session.ChatSessionId, Role = ChatRole.Assistant, InReplyToChatMessageId = q.ChatMessageId, MessageText = "answer",
                Status = reply, CitedSourcesJson = cited, Model = model, CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        return q.ChatMessageId;
    }

    private static async Task DrainAsync(QuestionRepositoryService s)
    {
        while (await s.ProcessBatchAsync(200, default) > 0) { }
    }

    [Fact]
    public async Task Paraphrases_from_different_clients_share_a_group_and_nothing_identifying_is_kept()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        var embeddings = new TopicEmbeddings(new() { ["change its name"] = RandomUnit(), ["directors"] = RandomUnit() });

        await using var db = Ctx();
        var coastal = await RequestAsync(db, $"Coastal{tag} Projects Limited", director: $"Surendra{tag} Sabbineni");
        var lifex = await RequestAsync(db, $"Lifex{tag} Healthcare Private Limited");
        var a = await AskAsync(db, coastal, $"When did Coastal{tag} Projects change its name?");
        var b = await AskAsync(db, lifex, $"when did Lifex{tag} Healthcare Private Limited change its name");
        var c = await AskAsync(db, coastal, $"Is Surendra{tag} Sabbineni one of the directors?", cited: "[]", model: ChatService.DocumentIndexModel);

        await DrainAsync(Service(db, embeddings));

        var entries = await db.ChatQuestionEntries.AsNoTracking().Include(e => e.Group)
            .Where(e => e.ChatMessageId == a || e.ChatMessageId == b || e.ChatMessageId == c).ToDictionaryAsync(e => e.ChatMessageId);
        Assert.Equal(3, entries.Count);
        Assert.Equal("When did [company] change its name?", entries[a].MaskedText);
        Assert.Equal("when did [company] change its name", entries[b].MaskedText);
        Assert.Equal("Is [person] one of the directors?", entries[c].MaskedText);
        Assert.DoesNotContain(embeddings.Embedded, t => t.Contains(tag, StringComparison.OrdinalIgnoreCase));   // only masked text leaves the building

        Assert.Equal(entries[a].ChatQuestionGroupId, entries[b].ChatQuestionGroupId);
        Assert.NotEqual(entries[a].ChatQuestionGroupId, entries[c].ChatQuestionGroupId);
        var nameChange = entries[a].Group!;
        Assert.Equal(2, nameChange.AskedCount);
        Assert.Equal(2, nameChange.ClientCount);
        Assert.Equal(2, nameChange.AnsweredWithEvidenceCount);
        Assert.Contains(nameChange.CanonicalQuestion, new[] { entries[a].MaskedText, entries[b].MaskedText });

        Assert.False(entries[c].AnsweredWithEvidence);
        Assert.True(entries[c].AnsweredFromIndex);
        Assert.Equal(1, entries[c].Group!.AnsweredFromIndexCount);
    }

    [Fact]
    public async Task Only_answered_questions_are_entered_and_each_once()
    {
        var embeddings = new TopicEmbeddings([]);
        await using var db = Ctx();
        var request = await RequestAsync(db, $"Unanswered{Guid.NewGuid():N} Limited");
        var noReply = await AskAsync(db, request, "What is the net worth?", reply: null);
        var failed = await AskAsync(db, request, "What is the net worth?", reply: ChatMessageStatus.Failed);
        var answered = await AskAsync(db, request, "What is the net worth?");

        await DrainAsync(Service(db, embeddings));
        await DrainAsync(Service(db, embeddings));   // a second pass enters nothing again

        var ids = await db.ChatQuestionEntries.AsNoTracking().Where(e => e.RequestId == request).Select(e => e.ChatMessageId).ToListAsync();
        Assert.Equal([answered], ids);
        Assert.DoesNotContain(noReply, ids);
        Assert.DoesNotContain(failed, ids);
    }

    [Fact]
    public async Task The_daily_cap_stops_further_embedding()
    {
        var embeddings = new TopicEmbeddings([]);
        await using var db = Ctx();
        var request = await RequestAsync(db, $"Capped{Guid.NewGuid():N} Limited");
        await AskAsync(db, request, "Who audits the accounts?");
        var usedToday = await db.ChatQuestionEntries.CountAsync(e => e.ProcessedUtc > DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, await Service(db, embeddings, maxPerDay: usedToday).ProcessBatchAsync(50, default));
        Assert.Empty(embeddings.Embedded);
    }

    [Fact]
    public async Task Suggestions_are_frequent_well_answered_questions_worded_for_any_company()
    {
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var db = Ctx();
        ChatQuestionGroup Group(string q, int clients, int asked, int evidence) => new()
        {
            CanonicalQuestion = q, Centroid = new SqlVector<float>(RandomUnit()), ClientCount = clients, AskedCount = asked, AnsweredWithEvidenceCount = evidence,
            FirstAskedUtc = DateTime.UtcNow, LastAskedUtc = DateTime.UtcNow
        };
        var groups = new[]
        {
            Group($"what is [company]'s net worth {tag}a?", clients: 9, asked: 20, evidence: 19),
            Group($"Who is the auditor {tag}b?", clients: 2, asked: 30, evidence: 30),                 // too few clients
            Group($"What did [company] borrow {tag}c?", clients: 5, asked: 10, evidence: 5),           // answered with evidence only half the time
            Group($"Is [person] disqualified {tag}d?", clients: 7, asked: 10, evidence: 10)            // names someone
        };
        db.ChatQuestionGroups.AddRange(groups);
        await db.SaveChangesAsync();
        try
        {
            var s = await QuestionRepositoryService.FrequentQuestionsAsync(db, new QuestionRepositoryOptions(), 50, default);
            Assert.Contains($"What is the company's net worth {tag}a?", s);
            Assert.DoesNotContain(s, q => q.Contains(tag + "b") || q.Contains(tag + "c") || q.Contains(tag + "d"));
        }
        finally
        {
            // Made-up groups would crowd the shared database's top list on later runs.
            db.ChatQuestionGroups.RemoveRange(groups);
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Ineligible_groups_at_the_top_do_not_hide_an_eligible_one_below()
    {
        // Review of #511: the limit applies after the evidence and placeholder filters.
        var tag = Guid.NewGuid().ToString("N")[..6];
        await using var db = Ctx();
        ChatQuestionGroup Group(string q, int clients, int evidence) => new()
        {
            CanonicalQuestion = q, Centroid = new SqlVector<float>(RandomUnit()), ClientCount = clients, AskedCount = 10, AnsweredWithEvidenceCount = evidence,
            FirstAskedUtc = DateTime.UtcNow, LastAskedUtc = DateTime.UtcNow
        };
        var groups = Enumerable.Range(0, 8).Select(i => Group($"Weak answer {tag} {i}?", clients: 1_000_000, evidence: 2))
            .Append(Group($"Ask about [person] {tag}?", clients: 1_000_000, evidence: 10))
            .Append(Group($"Strong answer {tag}?", clients: 3, evidence: 10)).ToList();
        db.ChatQuestionGroups.AddRange(groups);
        await db.SaveChangesAsync();
        try
        {
            var s = await QuestionRepositoryService.FrequentQuestionsAsync(db, new QuestionRepositoryOptions(), 3, default);
            Assert.DoesNotContain(s, q => q.Contains(tag) && !q.StartsWith("Strong", StringComparison.Ordinal));
            Assert.Contains($"Strong answer {tag}?", await QuestionRepositoryService.FrequentQuestionsAsync(db, new QuestionRepositoryOptions(), 10_000, default));
        }
        finally
        {
            db.ChatQuestionGroups.RemoveRange(groups);
            await db.SaveChangesAsync();
        }
    }
}
