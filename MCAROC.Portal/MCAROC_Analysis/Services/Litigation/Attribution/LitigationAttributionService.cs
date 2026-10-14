using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

public sealed record AttributionResult(int Assessed, int Attributed, int NotTheCompany, int Ambiguous, int Pending, int Judged, bool Skipped = false);

/// <summary>
/// Decides, for every case of a request, whether it is the company's (#489, decision a: the litigation data service searches non-exact, so a case it
/// returns is not the company's until a party is shown to be the company). Rules first (<see cref="CaseAttributionRules"/>); a party they cannot settle is
/// judged once (<see cref="PartyAttributionJudge"/>) and the verdict is cached for that company and party name, so a refresh or another request for the same
/// company costs nothing for parties already judged. Only attributed cases are shown anywhere (<see cref="LitigationAttribution.IsShown"/>); the others are
/// kept, with their reason, and listed apart.
/// </summary>
public sealed class LitigationAttributionService(AppDbContext db, IServiceProvider services, ILogger<LitigationAttributionService> logger)
{
    private const int TimeoutSeconds = 120;

    public async Task<AttributionResult> AttributeRequestAsync(long requestId, CancellationToken ct)
    {
        // One assessment of a request at a time (worker, pipeline and a refresh can all ask): the second finds nothing outstanding.
        await using var requestLock = await SqlSessionLock.TryAcquireAsync(db, $"MCAROC:LitigationAttribution:{requestId}", ct);
        if (requestLock is null) return new(0, 0, 0, 0, 0, 0, Skipped: true);

        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId)
            .Select(r => new { r.CompanyName, r.Cin, r.Llpin, r.LatestCompletedIngestionRunId }).FirstOrDefaultAsync(ct);
        if (request is null || string.IsNullOrWhiteSpace(request.CompanyName)) return new(0, 0, 0, 0, 0, 0, Skipped: true);

        var cases = await db.LitigationCases.Where(c => c.RequestId == requestId).Where(LitigationAttribution.IsOutstanding).ToListAsync(ct);
        if (cases.Count == 0) return new(0, 0, 0, 0, 0, 0);

        var subject = await SubjectAsync(requestId, request.CompanyName!, request.Cin ?? request.Llpin, request.LatestCompletedIngestionRunId, ct);
        var subjectKey = SubjectKey(request.Cin ?? request.Llpin, request.CompanyName!);
        var now = DateTime.UtcNow;
        var before = cases.ToDictionary(c => c.LitigationCaseId, c => LitigationAttribution.Shown(c.AttributionOutcome));

        // 1. Rules.
        var needing = new List<(LitigationCase Case, IReadOnlyList<string> Candidates, RuleAssessment Rules)>();
        foreach (var c in cases)
        {
            var assessment = CaseAttributionRules.Assess(PartiesOf(c), [subject.LegalName], subject.FormerNames);
            if (assessment.NeedsJudgement) needing.Add((c, assessment.Candidates, assessment));
            else Settle(c, assessment.Outcome!.Value, assessment.Rule, assessment.Party, assessment.Reason, now);
        }

        // 2. Judgements: cached ones first, the rest asked once per distinct party name.
        var judged = 0;
        if (needing.Count > 0)
        {
            var keys = needing.SelectMany(n => n.Candidates).Select(p => (Key: PartyKey(p), Name: p)).Where(x => x.Key.Length > 0)
                .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().Name);
            var verdicts = await db.LitigationPartyVerdicts.AsNoTracking()
                .Where(v => v.SubjectKey == subjectKey && v.PromptVersion == PartyAttributionJudge.PromptVersion && keys.Keys.Contains(v.PartyKey))
                .ToDictionaryAsync(v => v.PartyKey, v => (v.Verdict, v.Reason, v.PartyName), ct);
            var unjudged = keys.Where(k => !verdicts.ContainsKey(k.Key)).ToList();
            if (unjudged.Count > 0)
            {
                var fresh = await JudgeAsync(requestId, subject, subjectKey, unjudged.Select(u => u.Value).ToList(), ct);
                judged = fresh.Count;
                foreach (var (key, verdict) in fresh) verdicts[key] = verdict;
            }

            foreach (var (c, candidates, rules) in needing)
            {
                var found = candidates.Select(p => (Party: p, Has: verdicts.TryGetValue(PartyKey(p), out var v), V: v)).Where(x => x.Has).ToList();
                if (found.FirstOrDefault(x => x.V.Verdict == LitigationPartyVerdictKind.Same) is { Has: true } same)
                    Settle(c, LitigationAttributionOutcome.Attributed, "judged-same", same.Party, same.V.Reason, now);
                else if (found.Count < candidates.Count)
                    Settle(c, LitigationAttributionOutcome.Pending, null, null, "Waiting for the judgement of whether a party is the company.", c.AttributionDecidedUtc ?? now, keepSince: true);
                else if (found.FirstOrDefault(x => x.V.Verdict == LitigationPartyVerdictKind.Unsure) is { Has: true } unsure)
                    Settle(c, LitigationAttributionOutcome.Ambiguous, "judged-unsure", unsure.Party, unsure.V.Reason, now);
                else if (rules.Truncated)
                    // Every judged party is someone else, but not every party that could be the company was judged: unconfirmed, never "not the company's".
                    Settle(c, LitigationAttributionOutcome.Ambiguous, "judged-partial", null,
                        $"The {candidates.Count} likeliest of {rules.PossibleCount} parties that could be the company were checked and none is; the others were not checked.", now);
                else
                    Settle(c, LitigationAttributionOutcome.NotTheCompany, "judged-different", found.FirstOrDefault().Party, found.FirstOrDefault().V.Reason, now);
            }
        }

        await db.SaveChangesAsync(ct);
        // What a request shows changed: its caches (page, report, chat, charge links) must not keep the old set.
        if (cases.Any(c => LitigationAttribution.Shown(c.AttributionOutcome) != before[c.LitigationCaseId]))
            await RequestLiveData.BumpAsync(db, requestId, ct);

        var result = new AttributionResult(cases.Count,
            cases.Count(c => c.AttributionOutcome == LitigationAttributionOutcome.Attributed),
            cases.Count(c => c.AttributionOutcome == LitigationAttributionOutcome.NotTheCompany),
            cases.Count(c => c.AttributionOutcome == LitigationAttributionOutcome.Ambiguous),
            cases.Count(c => c.AttributionOutcome == LitigationAttributionOutcome.Pending), judged);
        logger.LogInformation("Litigation attribution for request {RequestId}: {Assessed} assessed, {Attributed} the company's, {Not} not, {Ambiguous} unclear, {Pending} waiting; {Judged} party names judged",
            requestId, result.Assessed, result.Attributed, result.NotTheCompany, result.Ambiguous, result.Pending, result.Judged);
        return result;
    }

    private static void Settle(LitigationCase c, LitigationAttributionOutcome outcome, string? rule, string? party, string? reason, DateTime when, bool keepSince = false)
    {
        c.AttributionOutcome = outcome;
        c.AttributionRule = rule;
        c.AttributionParty = Truncate(party, 500);
        c.AttributionReason = Truncate(reason, 600);
        c.AttributionDecidedUtc = keepSince ? c.AttributionDecidedUtc ?? when : when;
    }

    /// <summary>Judges the names in batches and stores each verdict. A batch whose call fails is left unjudged (its cases stay waiting and are tried again).</summary>
    private async Task<Dictionary<string, (LitigationPartyVerdictKind Verdict, string? Reason, string PartyName)>> JudgeAsync(
        long requestId, AttributionSubject subject, string subjectKey, IReadOnlyList<string> parties, CancellationToken ct)
    {
        var result = new Dictionary<string, (LitigationPartyVerdictKind, string?, string)>(StringComparer.Ordinal);
        ILitigationAiAnalysisClient client;
        try { client = services.GetRequiredService<ILitigationAiAnalysisClient>(); }
        catch (Exception ex)
        {
            logger.LogWarning("Litigation attribution for request {RequestId} cannot judge {Count} party names: the AI client is not configured ({Error})", requestId, parties.Count, ex.Message);
            return result;
        }

        foreach (var batch in parties.Chunk(PartyAttributionJudge.BatchSize))
        {
            LitigationAiCallResult call;
            using (AiUsageContext.Begin(requestId, AiUsagePurposes.LitigationAttribution))
                call = await client.CallAsync(PartyAttributionJudge.BuildPrompt(subject, batch), TimeoutSeconds, ct);
            if (!call.Success)
            {
                logger.LogWarning("Litigation attribution for request {RequestId}: a judgement of {Count} party names failed ({Reason}); they are tried again later",
                    requestId, batch.Length, call.FailureReason);
                continue;
            }
            foreach (var j in PartyAttributionJudge.Read(batch, call.RawResponse))
            {
                var key = PartyKey(j.Party);
                if (key.Length == 0) continue;
                result[key] = (j.Verdict, j.Reason, j.Party);
                db.LitigationPartyVerdicts.Add(new LitigationPartyVerdict
                {
                    SubjectKey = subjectKey, PartyKey = Truncate(key, 400)!, PartyName = Truncate(j.Party, 500)!, PromptVersion = PartyAttributionJudge.PromptVersion,
                    Model = VertexLitigationAiAnalysisClient.ModelId, Verdict = j.Verdict, Reason = Truncate(j.Reason, 600), CreatedUtc = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync(ct);
        }
        return result;
    }

    private async Task<AttributionSubject> SubjectAsync(long requestId, string companyName, string? identifier, long? runId, CancellationToken ct)
    {
        var former = await db.CompanyNameHistories.AsNoTracking()
            .Where(h => h.RequestId == requestId && (runId == null || h.IngestionRunId == runId) && h.PreviousName != "")
            .Select(h => h.PreviousName).Distinct().ToListAsync(ct);
        string? office = null;
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            var id = identifier.Trim().ToUpperInvariant();
            var row = await db.CompanyMasterRecords.AsNoTracking().Where(m => m.Identifier == id)
                .Select(m => new { m.Address, m.District, m.State }).FirstOrDefaultAsync(ct);
            if (row is not null)
                office = string.Join(", ", new[] { row.Address, row.District, row.State }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return new AttributionSubject(companyName, former, identifier, office);
    }

    /// <summary>The names of a case's parties, petitioners then respondents, as the record writes them.</summary>
    internal static IReadOnlyList<string> PartiesOf(LitigationCase c) => [.. Names(c.PetitionersJson), .. Names(c.RespondentsJson)];

    private static IEnumerable<string> Names(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var names = new List<string>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.ValueKind switch
                {
                    JsonValueKind.String => e.GetString(),
                    JsonValueKind.Object when e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name.Trim());
            }
            return names;
        }
        catch (JsonException) { return []; }
    }

    internal static string PartyKey(string party) => WholeNamePartyMatcher.Canonical(party) is { Length: > 0 } k ? k : party.Trim().ToUpperInvariant();

    internal static string SubjectKey(string? identifier, string companyName) =>
        !string.IsNullOrWhiteSpace(identifier) ? identifier.Trim().ToUpperInvariant()
            : "NAME:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(companyName.Trim().ToUpperInvariant())))[..40];

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
