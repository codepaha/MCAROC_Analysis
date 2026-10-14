using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>
/// The cross-client question repository (#499). Each answered chat question is masked (<see cref="QuestionMasker"/>: no company, person, identifier,
/// case number or amount survives), embedded once, and put in the group whose centre it is nearest — or a new group when none is near enough. A group's
/// name is its member nearest the centre (a real question, no naming call). Only masked text and its embedding are kept here; the client's own text stays
/// in its chat history.
/// </summary>
public sealed class QuestionRepositoryService(AppDbContext db, EmbeddingService embeddings, IOptions<QuestionRepositoryOptions> options, TimeProvider time)
{
    /// <summary>Enters up to <paramref name="batchSize"/> answered questions not yet in the repository. Returns how many were entered; 0 means none are
    /// waiting or today's cap is used.</summary>
    public async Task<int> ProcessBatchAsync(int batchSize, CancellationToken ct)
    {
        var opts = options.Value;
        var now = time.GetUtcNow().UtcDateTime;
        var usedToday = await db.ChatQuestionEntries.CountAsync(e => e.ProcessedUtc > now.AddDays(-1), ct);
        var take = Math.Min(batchSize, Math.Max(0, opts.MaxQuestionsPerDay - usedToday));
        if (take == 0) return 0;

        // An answered question: a user turn with a reply. A question whose reply failed is not entered (it has no outcome to count).
        var pending = await (
            from q in db.ChatMessages.AsNoTracking()
            join s in db.ChatSessions on q.ChatSessionId equals s.ChatSessionId
            join r in db.Requests on s.RequestId equals r.RequestId
            let reply = db.ChatMessages.Where(a => a.InReplyToChatMessageId == q.ChatMessageId && a.Role == ChatRole.Assistant && a.Status == ChatMessageStatus.Success)
                .OrderByDescending(a => a.ChatMessageId).Select(a => new { a.CitedSourcesJson, a.Model }).FirstOrDefault()
            where q.Role == ChatRole.User && reply != null && !db.ChatQuestionEntries.Any(e => e.ChatMessageId == q.ChatMessageId)
            orderby q.ChatMessageId
            select new { q.ChatMessageId, q.MessageText, q.CreatedDate, s.RequestId, r.ClientId, reply.CitedSourcesJson, reply.Model })
            .Take(take).ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var contexts = new Dictionary<long, MaskContext>();
        foreach (var requestId in pending.Select(p => p.RequestId).Distinct())
            contexts[requestId] = await MaskContextAsync(requestId, ct);
        var masked = pending.Select(p => Truncate(QuestionMasker.Mask(p.MessageText, contexts[p.RequestId]), 1000)).ToList();

        List<float[]> vectors;
        using (AiUsageContext.Begin(null, AiUsagePurposes.QuestionRepository))
            vectors = await embeddings.EmbedDocumentsAsync(masked, ct);

        // Every group's centre, compared in memory (dot product of unit vectors = cosine). Groups made in this batch join the list as they are made.
        var groups = await db.ChatQuestionGroups.ToListAsync(ct);
        var centres = groups.ToDictionary(g => g, g => g.Centroid.Memory.ToArray());
        var touched = new HashSet<ChatQuestionGroup>();
        for (var i = 0; i < pending.Count; i++)
        {
            var p = pending[i];
            var v = vectors[i];
            ChatQuestionGroup? best = null;
            var bestScore = double.MinValue;
            foreach (var (g, c) in centres)
            {
                var score = Dot(v, c);
                if (score > bestScore) { bestScore = score; best = g; }
            }
            if (best is null || bestScore < opts.SimilarityThreshold)
            {
                best = new ChatQuestionGroup { CanonicalQuestion = masked[i], Centroid = new SqlVector<float>(v), FirstAskedUtc = p.CreatedDate, LastAskedUtc = p.CreatedDate };
                db.ChatQuestionGroups.Add(best);
                centres[best] = v;
            }
            best.Entries.Add(new ChatQuestionEntry
            {
                ChatMessageId = p.ChatMessageId, RequestId = p.RequestId, ClientId = p.ClientId, MaskedText = masked[i], Embedding = new SqlVector<float>(v),
                AnsweredWithEvidence = HasCitations(p.CitedSourcesJson), AnsweredFromIndex = p.Model == ChatService.DocumentIndexModel,
                AskedUtc = p.CreatedDate, ProcessedUtc = now
            });
            touched.Add(best);
        }

        // Centre, name and counts of each group that changed, from its saved members and the new ones, saved together with the new entries in one
        // SaveChanges (one transaction): a failure leaves neither, and the next pass enters the questions again (review of #511).
        foreach (var g in touched)
        {
            var members = g.ChatQuestionGroupId == 0 ? [] : await db.ChatQuestionEntries.AsNoTracking().Where(e => e.ChatQuestionGroupId == g.ChatQuestionGroupId)
                .Select(e => new Member(e.MaskedText, e.Embedding, e.ClientId, e.AnsweredWithEvidence, e.AnsweredFromIndex, e.AskedUtc)).ToListAsync(ct);
            members.AddRange(g.Entries.Where(e => e.ChatQuestionEntryId == 0)
                .Select(e => new Member(e.MaskedText, e.Embedding, e.ClientId, e.AnsweredWithEvidence, e.AnsweredFromIndex, e.AskedUtc)));
            var memberVectors = members.Select(m => m.Embedding.Memory.ToArray()).ToList();
            var centre = Normalize(Mean(memberVectors));
            g.Centroid = new SqlVector<float>(centre);
            g.CanonicalQuestion = members[Enumerable.Range(0, members.Count).MaxBy(j => Dot(memberVectors[j], centre))].MaskedText;
            g.AskedCount = members.Count;
            g.ClientCount = members.Select(m => m.ClientId).Distinct().Count();
            g.AnsweredWithEvidenceCount = members.Count(m => m.AnsweredWithEvidence);
            g.AnsweredFromIndexCount = members.Count(m => m.AnsweredFromIndex);
            g.FirstAskedUtc = members.Min(m => m.AskedUtc);
            g.LastAskedUtc = members.Max(m => m.AskedUtc);
        }
        await db.SaveChangesAsync(ct);
        return pending.Count;
    }

    /// <summary>
    /// The questions many clients ask and that are usually answered with evidence, worded for any company: only groups whose name has no placeholder
    /// other than [company] (which reads as "the company"), asked by at least <see cref="QuestionRepositoryOptions.SuggestMinClients"/> clients and answered
    /// with evidence at least <see cref="QuestionRepositoryOptions.SuggestMinEvidenceShare"/> of the time. Most clients first.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FrequentQuestionsAsync(AppDbContext db, QuestionRepositoryOptions opts, int max, CancellationToken ct)
    {
        // Every condition in the query, before the limit, so groups that fail it never crowd out eligible ones below them (review of #511).
        var groups = await db.ChatQuestionGroups.AsNoTracking()
            .Where(g => g.ClientCount >= opts.SuggestMinClients && g.AskedCount > 0
                && g.AnsweredWithEvidenceCount >= opts.SuggestMinEvidenceShare * g.AskedCount
                && !g.CanonicalQuestion.Replace(QuestionMasker.Company, "").Contains("["))
            .OrderByDescending(g => g.ClientCount).ThenByDescending(g => g.AskedCount)
            .Select(g => g.CanonicalQuestion).Take(max).ToListAsync(ct);
        return groups
            .Select(q => q.Replace(QuestionMasker.Company, "the company", StringComparison.Ordinal))
            .Select(q => q.Length > 0 ? char.ToUpperInvariant(q[0]) + q[1..] : q)
            .ToList();
    }

    /// <summary>The names to mask for one request: the company's current and former names and its directors'.</summary>
    private async Task<MaskContext> MaskContextAsync(long requestId, CancellationToken ct)
    {
        var companies = new List<string>();
        if (await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.CompanyName).FirstOrDefaultAsync(ct) is { Length: > 0 } current)
            companies.Add(current);
        companies.AddRange(await db.CompanyNameHistories.AsNoTracking().Where(h => h.RequestId == requestId && h.PreviousName != "")
            .Select(h => h.PreviousName).Distinct().ToListAsync(ct));
        var people = await db.Directors.AsNoTracking().Where(d => d.RequestId == requestId && d.NameRaw != "")
            .Select(d => d.NameRaw).Distinct().ToListAsync(ct);
        return new MaskContext(companies, people);
    }

    private sealed record Member(string MaskedText, SqlVector<float> Embedding, long ClientId, bool AnsweredWithEvidence, bool AnsweredFromIndex, DateTime AskedUtc);

    internal static bool HasCitations(string? citedSourcesJson) =>
        !string.IsNullOrWhiteSpace(citedSourcesJson) && citedSourcesJson.Trim() is not "[]" and not "null";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    internal static double Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (var i = 0; i < n; i++) sum += a[i] * b[i];
        return sum;
    }

    private static float[] Mean(IReadOnlyList<float[]> vectors)
    {
        var mean = new float[vectors[0].Length];
        foreach (var v in vectors)
            for (var i = 0; i < mean.Length; i++) mean[i] += v[i];
        for (var i = 0; i < mean.Length; i++) mean[i] /= vectors.Count;
        return mean;
    }

    private static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(Dot(v, v));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }
}

public sealed class QuestionRepositoryOptions
{
    public const string SectionName = "QuestionRepository";
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 50;
    public int PollMinutes { get; set; } = 30;
    public int StartDelaySeconds { get; set; } = 120;
    /// <summary>At most this many questions embedded in any 24 hours (one short embedding each, a fraction of a cent per thousand).</summary>
    public int MaxQuestionsPerDay { get; set; } = 2000;
    /// <summary>Cosine similarity at or above which a question joins a group.</summary>
    public double SimilarityThreshold { get; set; } = 0.85;
    /// <summary>A group is offered as a chat suggestion when this many clients asked it...</summary>
    public int SuggestMinClients { get; set; } = 3;
    /// <summary>...and at least this share of its answers cited evidence.</summary>
    public double SuggestMinEvidenceShare { get; set; } = 0.8;
}

/// <summary>Every <see cref="QuestionRepositoryOptions.PollMinutes"/>: enters the answered chat questions not yet in the repository. A failed pass is logged
/// and the next one tries again.</summary>
public sealed class QuestionRepositoryWorker(IServiceScopeFactory scopes, IOptions<QuestionRepositoryOptions> options, ILogger<QuestionRepositoryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, opts.StartDelaySeconds)), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var entered = 0;
                while (!stoppingToken.IsCancellationRequested)
                {
                    using var scope = scopes.CreateScope();
                    var n = await scope.ServiceProvider.GetRequiredService<QuestionRepositoryService>().ProcessBatchAsync(Math.Max(1, opts.BatchSize), stoppingToken);
                    entered += n;
                    if (n == 0) break;
                }
                if (entered > 0) logger.LogInformation("Question repository: {Count} questions entered", entered);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Question repository pass failed; the next pass tries again."); }

            try { await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, opts.PollMinutes)), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
