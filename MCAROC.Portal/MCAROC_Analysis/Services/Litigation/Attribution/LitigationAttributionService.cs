using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.McaFilings;
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
        // The search can finish before ingestion (it runs in parallel with the fetch); the former names judged
        // against come from ingestion, so hold off while one is still coming.
        if (request.LatestCompletedIngestionRunId is null && await IngestionExpectedAsync(requestId, ct)) return new(0, 0, 0, 0, 0, 0, Skipped: true);

        var cases = await db.LitigationCases.Where(c => c.RequestId == requestId).Where(LitigationAttribution.IsOutstanding).ToListAsync(ct);
        if (cases.Count == 0) return new(0, 0, 0, 0, 0, 0);

        var (subject, names) = await SubjectAsync(requestId, request.CompanyName!, request.Cin ?? request.Llpin, request.LatestCompletedIngestionRunId, ct);
        // Name variations people added for the company: a party that is exactly one is the company's (CaseAttributionRules.NameVariationRule).
        var variations = await LitigationCompanyNames.ActiveVariationsAsync(db, request.Cin ?? request.Llpin, ct);
        var variationNames = variations.Select(v => v.Name).ToList();
        var subjectKey = SubjectKey(request.Cin ?? request.Llpin, request.CompanyName!);
        var now = DateTime.UtcNow;
        var before = cases.ToDictionary(c => c.LitigationCaseId, c => LitigationAttribution.Shown(c.AttributionOutcome));

        // 1. Rules; a party that is one of the company's names is checked against when the company had that name (#497).
        var needing = new List<(LitigationCase Case, IReadOnlyList<string> Candidates, RuleAssessment Rules)>();
        var held = new List<FormerNameCaseQuestion>();
        var holders = new Dictionary<(string Name, bool Former), (IReadOnlyList<NameHolder> Others, bool Known)>();
        foreach (var c in cases)
        {
            var assessment = CaseAttributionRules.Assess(PartiesOf(c), [subject.LegalName], subject.FormerNames, variationNames);
            if (assessment.Rule == CaseAttributionRules.NameVariationRule
                && variations.FirstOrDefault(v => v.Name == assessment.MatchedName) is { } added)
                assessment = assessment with { Reason = $"A party is the name variation \"{added.Name}\" added for the company by {added.AddedBy} on {Ist.Date(added.AddedUtc)}." };
            if (assessment.NeedsJudgement) { needing.Add((c, assessment.Candidates, assessment)); continue; }
            if (assessment.Outcome == LitigationAttributionOutcome.Attributed && assessment.MatchedName is { } matched
                && (FormerNameCaseRules.IsFormerNameRule(assessment.Rule) || FormerNameCaseRules.IsCurrentNameRule(assessment.Rule)))
            {
                var former = FormerNameCaseRules.IsFormerNameRule(assessment.Rule);
                var filed = LitigationCaseAges.ParseDate(c.FilingDate);
                var name = new NameAtFiling(matched, former, FormerNameCaseRules.UsedUntil(names.Changes, matched),
                    FormerNameCaseRules.CurrentSince(names.Changes, names.Incorporated), []);
                // Who else has the name is looked up only when the dates alone could leave the case to another company.
                if (FormerNameCaseRules.MayBeAnothersName(name, filed))
                {
                    if (!holders.TryGetValue((matched, former), out var found))
                        holders[(matched, former)] = found = await OtherHoldersAsync(matched, former, subject.Identifier, ct);
                    name = name with { Others = found.Others, OthersKnown = found.Known };
                }
                var check = FormerNameCaseRules.Check(name, filed);
                if (check.Decision == FormerNameCaseDecision.NeedsJudgement)
                {
                    held.Add(new FormerNameCaseQuestion(c.LitigationCaseId, assessment.Party!, name, c.Court, c.CaseNumber, c.FilingDate,
                        string.Join(", ", new[] { c.District, c.State }.Where(x => !string.IsNullOrWhiteSpace(x))),
                        Names(c.PetitionersJson).ToList(), Names(c.RespondentsJson).ToList()));
                    Settle(c, LitigationAttributionOutcome.Pending, null, assessment.Party, check.Reason, c.AttributionDecidedUtc ?? now, keepSince: true);
                    continue;
                }
                if (check.Reason.Length > 0) assessment = assessment with { Rule = check.Rule ?? assessment.Rule, Reason = check.Reason };
            }
            Settle(c, assessment.Outcome!.Value, assessment.Rule, assessment.Party, assessment.Reason, now);
        }
        var judged = held.Count > 0 ? await SettleHeldAsync(requestId, subject, subjectKey, names.Changes, held, cases, now, ct) : 0;

        // 2. Judgements: cached ones first, the rest asked once per distinct party name.
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
                judged += fresh.Count;
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

    /// <summary>#497: the cases held out because another company has (or had) the name a party matched. A stored judgement for the case is used; the rest
    /// are judged in batches and each verdict stored. A case whose judgement is not had (no client, a failed call) stays waiting, not counted.</summary>
    private async Task<int> SettleHeldAsync(long requestId, AttributionSubject subject, string subjectKey, IReadOnlyList<NameChange> changes,
        IReadOnlyList<FormerNameCaseQuestion> held, IReadOnlyList<LitigationCase> cases, DateTime now, CancellationToken ct)
    {
        var keys = held.Select(q => FormerNameCaseJudge.VerdictKey(q.CaseId)).ToList();
        var verdicts = await db.LitigationPartyVerdicts.AsNoTracking()
            .Where(v => v.SubjectKey == subjectKey && v.PromptVersion == FormerNameCaseJudge.PromptVersion && keys.Contains(v.PartyKey))
            .ToDictionaryAsync(v => v.PartyKey, v => (v.Verdict, v.Reason), ct);

        var asked = held.Where(q => !verdicts.ContainsKey(FormerNameCaseJudge.VerdictKey(q.CaseId))).ToList();
        var judged = 0;
        var client = asked.Count > 0 ? services.GetService<ILitigationAiAnalysisClient>() : null;
        if (asked.Count > 0 && client is null)
            logger.LogWarning("Litigation attribution for request {RequestId} cannot judge {Count} cases under a shared name: the AI client is not configured", requestId, asked.Count);
        foreach (var batch in client is null ? [] : asked.Chunk(FormerNameCaseJudge.BatchSize))
        {
            LitigationAiCallResult call;
            using (AiUsageContext.Begin(requestId, AiUsagePurposes.LitigationAttribution))
                call = await client!.CallAsync(FormerNameCaseJudge.BuildPrompt(subject, changes, batch), TimeoutSeconds, ct);
            if (!call.Success)
            {
                logger.LogWarning("Litigation attribution for request {RequestId}: a judgement of {Count} cases under a shared name failed ({Reason}); they are tried again later",
                    requestId, batch.Length, call.FailureReason);
                continue;
            }
            foreach (var (caseId, verdict, reason) in FormerNameCaseJudge.Read(batch, call.RawResponse))
            {
                var key = FormerNameCaseJudge.VerdictKey(caseId);
                verdicts[key] = (verdict, reason);
                judged++;
                db.LitigationPartyVerdicts.Add(new LitigationPartyVerdict
                {
                    SubjectKey = subjectKey, PartyKey = key, PartyName = Truncate(batch.First(q => q.CaseId == caseId).Party, 500)!,
                    PromptVersion = FormerNameCaseJudge.PromptVersion, Model = VertexLitigationAiAnalysisClient.ModelId, Verdict = verdict,
                    Reason = Truncate(reason, 600), CreatedUtc = DateTime.UtcNow
                });
            }
            await db.SaveChangesAsync(ct);
        }

        foreach (var q in held)
        {
            if (!verdicts.TryGetValue(FormerNameCaseJudge.VerdictKey(q.CaseId), out var v)) continue;   // still waiting, as the rules left it
            var c = cases.First(x => x.LitigationCaseId == q.CaseId);
            var (outcome, rule) = v.Verdict switch
            {
                LitigationPartyVerdictKind.Same => (LitigationAttributionOutcome.Attributed, "judged-shared-name-company"),
                LitigationPartyVerdictKind.Different => (LitigationAttributionOutcome.NotTheCompany, "judged-shared-name-other"),
                _ => (LitigationAttributionOutcome.Ambiguous, "judged-shared-name-unsure"),
            };
            Settle(c, outcome, rule, q.Party, v.Reason, now);
        }
        return judged;
    }

    /// <summary>The other companies with <paramref name="name"/>: for a former name, those that have it today (the company master, and the reference
    /// tool's search where it is configured); for the current name, those that had it before (the search's former-name hits). The company itself is
    /// never one. Without the company's identifier none can be told apart from it, so none is returned and the dates alone decide.
    /// <para>Known is false when the search was made but not read to its end, or failed: an empty list is then not evidence that nobody else has the
    /// name (review of #536). A search that is not configured at all leaves the master alone to say, as decided.</para></summary>
    private async Task<(IReadOnlyList<NameHolder> Others, bool Known)> OtherHoldersAsync(string name, bool former, string? identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return ([], true);
        var self = identifier.Trim().ToUpperInvariant();
        var normalized = CompanyNameNormalizer.Normalize(name).NameNormalized;
        var holders = new List<NameHolder>();
        var known = true;
        if (former && normalized.Length > 0)
            holders.AddRange(await db.CompanyMasterRecords.AsNoTracking().Where(m => m.NameNormalized == normalized && m.Identifier != self)
                .Select(m => new NameHolder(m.Identifier, m.Name, m.State)).Take(10).ToListAsync(ct));
        if (services.GetService<IFormerNameLookup>() is { IsConfigured: true } lookup)
        {
            try
            {
                var found = await lookup.FindAsync(name, ct);
                known = found.Searched && found.Complete;
                // The search returns every hit it saw; only those with exactly this name count (review of #536: "ALPHA TRADING LIMITED" is not "ALPHA LIMITED").
                bool Same(string? other) => CompanyNameNormalizer.Normalize(other).NameNormalized == normalized;
                holders.AddRange(former
                    ? found.CurrentHolders.Where(h => !string.IsNullOrWhiteSpace(h.Identifier) && Same(h.Name)).Select(h => new NameHolder(h.Identifier!, h.Name, h.State))
                    : found.FormerNames.Where(h => !string.IsNullOrWhiteSpace(h.Identifier) && Same(h.FormerName))
                        .Select(h => new NameHolder(h.Identifier!, h.CurrentName ?? h.FormerName, h.State)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                known = false;
                logger.LogWarning(ex, "Looking up other companies named '{Name}' failed; whether another company has it is not known", name);
            }
        }
        return (holders.Where(h => !string.Equals(h.Identifier.Trim(), self, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(h => h.Identifier.Trim().ToUpperInvariant()).ToList(), known);
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

    /// <summary>An ingestion is running, or an auto-fetch that will end in one is still in progress.</summary>
    private async Task<bool> IngestionExpectedAsync(long requestId, CancellationToken ct) =>
        await db.IngestionRuns.AsNoTracking().AnyAsync(i => i.RequestId == requestId && i.Status == IngestionRunStatus.Running, ct)
        || await db.AutoFetchJobs.AsNoTracking().AnyAsync(j => j.RequestId == requestId && j.Status != AutoFetchJobStatus.Completed
            && j.Status != AutoFetchJobStatus.CompletedWithWarnings && j.Status != AutoFetchJobStatus.Failed, ct);

    /// <summary>The company's name changes with their dates (the name history checked against the certificates of change of name, #497) and the day it
    /// was incorporated.</summary>
    private sealed record SubjectNames(IReadOnlyList<NameChange> Changes, DateOnly? Incorporated);

    /// <summary>The company the parties are judged against. Its former names are the name history's and any a certificate of change of name states.</summary>
    private async Task<(AttributionSubject Subject, SubjectNames Names)> SubjectAsync(long requestId, string companyName, string? identifier, long? runId, CancellationToken ct)
    {
        var history = await db.CompanyNameHistories.AsNoTracking()
            .Where(h => h.RequestId == requestId && (runId == null || h.IngestionRunId == runId) && h.PreviousName != "")
            .OrderBy(h => h.DisplayOrder).ToListAsync(ct);
        IReadOnlyList<CertificateNameChange> certificates = [];
        try { certificates = await NameChangeCertificateLoader.LoadAsync(db, requestId, ct, services.GetService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the change-of-name certificates failed for request {RequestId}; the name history alone is used", requestId);
        }
        var changes = NameChanges.Merge(history, companyName, certificates);
        var former = history.Select(h => h.PreviousName).Concat(changes.Select(c => c.FromName))
            .Where(n => !string.IsNullOrWhiteSpace(n) && NameChanges.Key(n) != NameChanges.Key(companyName))
            .DistinctBy(NameChanges.Key).ToList();
        var incorporated = runId is null ? null
            : await db.CompanyProfiles.AsNoTracking().Where(p => p.IngestionRunId == runId).Select(p => p.IncorporationDate).FirstOrDefaultAsync(ct);
        string? office = null;
        if (!string.IsNullOrWhiteSpace(identifier))
        {
            var id = identifier.Trim().ToUpperInvariant();
            var row = await db.CompanyMasterRecords.AsNoTracking().Where(m => m.Identifier == id)
                .Select(m => new { m.Address, m.District, m.State }).FirstOrDefaultAsync(ct);
            if (row is not null)
                office = string.Join(", ", new[] { row.Address, row.District, row.State }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
        return (new AttributionSubject(companyName, former, identifier, office), new SubjectNames(changes, incorporated));
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
