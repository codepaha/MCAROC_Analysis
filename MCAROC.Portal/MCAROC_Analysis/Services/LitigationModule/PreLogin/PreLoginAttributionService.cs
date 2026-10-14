using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public sealed record PreLoginAttributionRunResult(
    int TotalCases, int Assessed, int Attributed, int NotTheCompany, int Ambiguous, int SuggestedExclusions, int AutoExcluded);

public sealed record TrustReportRow(
    string ReasonKind, int Suggested, int Accepted, int Rejected, int AutoApplied, int Undone, int AnalystMiss, double Precision);

public sealed record PreLoginTrustReport(
    long? JobId, IReadOnlyList<TrustReportRow> Rows, int TotalDecisions, double OverallPrecision);

/// <summary>
/// Pre-login litigation case attribution and automatic exclusion engine (#446).
/// Checks cases against their target:
/// - Company/LLP targets use CaseAttributionRules + cached AI verdicts (LitigationPartyVerdicts, CIN-keyed or name-based).
/// - Individual targets (directors/partners) use rules only until #452 (no AI calls).
/// Supports Suggest (default) and Apply modes.
/// Analyst choice always wins over automatic check.
/// </summary>
public sealed class PreLoginAttributionService(
    AppDbContext db,
    IOptions<PreLoginAttributionOptions> options,
    IServiceProvider services,
    ILogger<PreLoginAttributionService>? logger = null)
{
    private const int TimeoutSeconds = 120;

    public async Task<PreLoginAttributionRunResult> CheckAttributionAsync(long jobId, bool forceAll = false, CancellationToken ct = default)
    {
        var scope = await db.Set<ModuleScope>()
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

        if (scope is null)
        {
            logger?.LogWarning("No ModuleScope found for pre-login job {JobId}. Run backfill first.", jobId);
            return new PreLoginAttributionRunResult(0, 0, 0, 0, 0, 0, 0);
        }

        var scopeCases = await db.Set<ModuleScopeCase>()
            .Where(sc => sc.ScopeId == scope.ScopeId)
            .ToListAsync(ct);

        if (scopeCases.Count == 0)
        {
            return new PreLoginAttributionRunResult(0, 0, 0, 0, 0, 0, 0);
        }

        var targets = await db.PreLoginLitigationTargets
            .Where(t => t.PreLoginReportJobId == jobId)
            .ToDictionaryAsync(t => t.PreLoginLitigationTargetId, ct);

        var borrowerTarget = targets.Values.FirstOrDefault(t => t.TargetKind == PreLoginTargetKind.Borrower);

        var job = await db.PreLoginReportJobs.AsNoTracking()
            .FirstOrDefaultAsync(j => j.PreLoginReportJobId == jobId, ct);

        if (job is null || string.IsNullOrWhiteSpace(job.DataJson))
        {
            return new PreLoginAttributionRunResult(scopeCases.Count, 0, 0, 0, 0, 0, 0);
        }

        InstaReportData? data = null;
        try { data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson); }
        catch { }

        var legalCaseMap = (data?.LegalCases?.Cases ?? [])
            .Where(c => !string.IsNullOrWhiteSpace(c.CaseRowId))
            .ToDictionary(c => c.CaseRowId, StringComparer.OrdinalIgnoreCase);

        var mode = options.Value.Mode;
        int assessed = 0, attributed = 0, notCompany = 0, ambiguous = 0, suggested = 0, autoExcluded = 0;
        var now = DateTime.UtcNow;

        var needingJudgement = new List<(ModuleScopeCase ScopeCase, PreLoginLitigationTarget Target, IReadOnlyList<string> Candidates, RuleAssessment Assessment)>();

        foreach (var sc in scopeCases)
        {
            if (!forceAll && sc.AttributionOutcome != null && sc.AttributionOutcome != LitigationAttributionOutcome.Pending)
            {
                continue;
            }

            PreLoginLitigationTarget? target = null;
            if (sc.TargetId != null && targets.TryGetValue(sc.TargetId.Value, out var t))
            {
                target = t;
            }
            target ??= borrowerTarget;

            legalCaseMap.TryGetValue(sc.Alias, out var rec);
            var parties = ExtractParties(rec);

            if (target is null)
            {
                sc.AttributionOutcome = LitigationAttributionOutcome.Ambiguous;
                sc.AttributionRule = "no-target";
                sc.AttributionReason = "No target information available for this case.";
                sc.UpdatedUtc = now;
                assessed++;
                ambiguous++;
                continue;
            }

            if (target.TargetKind == PreLoginTargetKind.Borrower)
            {
                // Company / LLP target: rules first, then cached AI verdicts
                var assessment = CaseAttributionRules.Assess(parties, [target.TargetName], []);
                if (assessment.NeedsJudgement)
                {
                    needingJudgement.Add((sc, target, assessment.Candidates, assessment));
                }
                else
                {
                    ApplyOutcome(sc, target, assessment.Outcome!.Value, assessment.Rule, assessment.Party, assessment.Reason, mode, now, ref suggested, ref autoExcluded);
                    assessed++;
                    if (assessment.Outcome == LitigationAttributionOutcome.Attributed) attributed++;
                    else if (assessment.Outcome == LitigationAttributionOutcome.NotTheCompany) notCompany++;
                    else ambiguous++;
                }
            }
            else
            {
                // Individual target (Director): rules only until #452
                var (outcome, rule, party, reason) = AssessIndividual(parties, target.TargetName);
                ApplyOutcome(sc, target, outcome, rule, party, reason, mode, now, ref suggested, ref autoExcluded);
                assessed++;
                if (outcome == LitigationAttributionOutcome.Attributed) attributed++;
                else if (outcome == LitigationAttributionOutcome.NotTheCompany) notCompany++;
                else ambiguous++;
            }
        }

        // Handle judgements for company targets
        if (needingJudgement.Count > 0)
        {
            var targetGroups = needingJudgement.GroupBy(x => x.Target.PreLoginLitigationTargetId);
            foreach (var group in targetGroups)
            {
                var target = group.First().Target;
                var subjectKey = SubjectKey(job.Cin, target.TargetName);
                var distinctParties = group.SelectMany(x => x.Candidates)
                    .Select(p => (Key: LitigationAttributionService.PartyKey(p), Name: p))
                    .Where(x => x.Key.Length > 0)
                    .GroupBy(x => x.Key)
                    .ToDictionary(g => g.Key, g => g.First().Name);

                var verdicts = await db.LitigationPartyVerdicts.AsNoTracking()
                    .Where(v => v.SubjectKey == subjectKey && v.PromptVersion == PartyAttributionJudge.PromptVersion && distinctParties.Keys.Contains(v.PartyKey))
                    .ToDictionaryAsync(v => v.PartyKey, v => (v.Verdict, v.Reason, v.PartyName), ct);

                var unjudged = distinctParties.Where(k => !verdicts.ContainsKey(k.Key)).ToList();
                if (unjudged.Count > 0)
                {
                    var fresh = await JudgePartiesAsync(job.PreLoginReportJobId, target.TargetName, job.Cin, subjectKey, unjudged.Select(u => u.Value).ToList(), ct);
                    foreach (var (key, v) in fresh) verdicts[key] = v;
                }

                foreach (var (sc, t, candidates, rules) in group)
                {
                    assessed++;
                    var found = candidates.Select(p => (Party: p, Has: verdicts.TryGetValue(LitigationAttributionService.PartyKey(p), out var v), V: v)).Where(x => x.Has).ToList();
                    if (found.FirstOrDefault(x => x.V.Verdict == LitigationPartyVerdictKind.Same) is { Has: true } same)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Attributed, "judged-same", same.Party, same.V.Reason, mode, now, ref suggested, ref autoExcluded);
                        attributed++;
                    }
                    else if (found.Count < candidates.Count)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Pending, null, null, "Waiting for party attribution judgement.", mode, now, ref suggested, ref autoExcluded);
                        ambiguous++;
                    }
                    else if (found.FirstOrDefault(x => x.V.Verdict == LitigationPartyVerdictKind.Unsure) is { Has: true } unsure)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Ambiguous, "judged-unsure", unsure.Party, unsure.V.Reason, mode, now, ref suggested, ref autoExcluded);
                        ambiguous++;
                    }
                    else if (rules.Truncated)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Ambiguous, "judged-partial", null,
                            $"Checked {candidates.Count} candidate parties without finding target; others were not checked.", mode, now, ref suggested, ref autoExcluded);
                        ambiguous++;
                    }
                    else
                    {
                        var first = found.FirstOrDefault();
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.NotTheCompany, "judged-different", first.Party, first.V.Reason, mode, now, ref suggested, ref autoExcluded);
                        notCompany++;
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new PreLoginAttributionRunResult(scopeCases.Count, assessed, attributed, notCompany, ambiguous, suggested, autoExcluded);
    }

    private void ApplyOutcome(
        ModuleScopeCase sc,
        PreLoginLitigationTarget target,
        LitigationAttributionOutcome outcome,
        string? rule,
        string? party,
        string? reason,
        PreLoginAttributionMode mode,
        DateTime now,
        ref int suggested,
        ref int autoExcluded)
    {
        sc.AttributionOutcome = outcome;
        sc.AttributionRule = rule;
        sc.AttributionParty = Truncate(party, 500);
        sc.AttributionReason = Truncate(reason, 512);
        sc.TargetId = target.PreLoginLitigationTargetId;
        sc.UpdatedUtc = now;

        if (outcome == LitigationAttributionOutcome.NotTheCompany)
        {
            if (mode == PreLoginAttributionMode.Suggest)
            {
                suggested++;
                db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
                {
                    ScopeCaseId = sc.ScopeCaseId,
                    PreLoginReportJobId = target.PreLoginReportJobId,
                    CaseRowId = sc.Alias,
                    TargetId = target.PreLoginLitigationTargetId,
                    TargetKind = target.TargetKind,
                    PartyName = Truncate(party ?? string.Empty, 500)!,
                    ReasonKind = rule ?? "not-the-target",
                    Action = PreLoginAttributionAction.Suggested,
                    DecidedBy = "System",
                    DecidedUtc = now
                });
            }
            else if (mode == PreLoginAttributionMode.Apply)
            {
                autoExcluded++;
                sc.IsExcluded = true;
                if (string.IsNullOrWhiteSpace(sc.ExclusionReason))
                {
                    sc.ExclusionReason = $"[Auto] {reason}";
                }
                sc.IncludedInReport = false;

                db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
                {
                    ScopeCaseId = sc.ScopeCaseId,
                    PreLoginReportJobId = target.PreLoginReportJobId,
                    CaseRowId = sc.Alias,
                    TargetId = target.PreLoginLitigationTargetId,
                    TargetKind = target.TargetKind,
                    PartyName = Truncate(party ?? string.Empty, 500)!,
                    ReasonKind = rule ?? "not-the-target",
                    Action = PreLoginAttributionAction.AutomaticallyApplied,
                    DecidedBy = "System",
                    DecidedUtc = now
                });
            }
        }
    }

    private static (LitigationAttributionOutcome Outcome, string Rule, string? Party, string Reason) AssessIndividual(
        IReadOnlyList<string> parties, string individualName)
    {
        if (parties.Count == 0)
        {
            return (LitigationAttributionOutcome.Ambiguous, "no-parties", null, "The record lists no parties.");
        }

        var targetNorm = NormalizeName(individualName);
        if (string.IsNullOrWhiteSpace(targetNorm))
        {
            return (LitigationAttributionOutcome.Ambiguous, "invalid-target-name", null, "Invalid target individual name.");
        }

        foreach (var p in parties)
        {
            var pNorm = NormalizeName(p);
            if (pNorm == targetNorm || pNorm.Contains(targetNorm, StringComparison.OrdinalIgnoreCase))
            {
                return (LitigationAttributionOutcome.Attributed, "exact-individual-name", p, "A party matches the individual's name.");
            }
        }

        // Check if all parties are clearly corporate bodies/institutions
        bool allCorporate = parties.All(IsLikelyCorporateEntity);
        if (allCorporate)
        {
            return (LitigationAttributionOutcome.NotTheCompany, "corporate-entities-only", parties[0], "All listed parties are corporate entities or institutions, not the individual.");
        }

        return (LitigationAttributionOutcome.Ambiguous, "individual-unconfirmed", null, "Could not confirm whether a party is the individual.");
    }

    private static readonly HashSet<string> CorporateKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LTD", "LIMITED", "PVT", "PRIVATE", "LLP", "BANK", "CORPORATION", "CORP", "INC", "GOVERNMENT", "UNION OF INDIA", "STATE OF", "AUTHORITY", "BOARD", "TRUST", "SOCIETY"
    };

    private static bool IsLikelyCorporateEntity(string party)
    {
        var upper = party.ToUpperInvariant();
        return CorporateKeywords.Any(k => upper.Contains(k));
    }

    private static string NormalizeName(string name) =>
        string.Join(" ", name.Split(new[] { ' ', '.', ',', '-' }, StringSplitOptions.RemoveEmptyEntries)).Trim().ToUpperInvariant();

    public async Task<int> ApplyOpenSuggestionsAsync(long jobId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        var scope = await db.Set<ModuleScope>()
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

        if (scope is null) return 0;

        var openSuggestions = await db.Set<ModuleScopeCase>()
            .Where(sc => sc.ScopeId == scope.ScopeId &&
                         sc.AttributionOutcome == LitigationAttributionOutcome.NotTheCompany &&
                         !sc.IsExcluded)
            .ToListAsync(ct);

        if (openSuggestions.Count == 0) return 0;

        var targetIds = openSuggestions
            .Where(sc => sc.TargetId.HasValue)
            .Select(sc => sc.TargetId!.Value)
            .Distinct()
            .ToList();

        var targetKinds = targetIds.Count > 0
            ? await db.PreLoginLitigationTargets
                .AsNoTracking()
                .Where(t => targetIds.Contains(t.PreLoginLitigationTargetId))
                .ToDictionaryAsync(t => t.PreLoginLitigationTargetId, t => t.TargetKind, ct)
            : new Dictionary<long, PreLoginTargetKind>();

        var now = DateTime.UtcNow;
        foreach (var sc in openSuggestions)
        {
            sc.IsExcluded = true;
            if (string.IsNullOrWhiteSpace(sc.ExclusionReason))
            {
                sc.ExclusionReason = $"[Auto] {sc.AttributionReason}";
            }
            sc.IncludedInReport = false;
            sc.UpdatedUtc = now;

            var targetKind = sc.TargetId.HasValue && targetKinds.TryGetValue(sc.TargetId.Value, out var tk)
                ? tk
                : PreLoginTargetKind.Borrower;

            db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
            {
                ScopeCaseId = sc.ScopeCaseId,
                PreLoginReportJobId = jobId,
                CaseRowId = sc.Alias,
                TargetId = sc.TargetId ?? 0,
                TargetKind = targetKind,
                PartyName = Truncate(sc.AttributionParty ?? string.Empty, 500)!,
                ReasonKind = sc.AttributionRule ?? "not-the-target",
                Action = PreLoginAttributionAction.AutomaticallyApplied,
                DecidedBy = decidedBy,
                DecidedUtc = now
            });
        }

        await db.SaveChangesAsync(ct);
        return openSuggestions.Count;
    }

    public async Task<bool> AcceptSuggestionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
        if (sc is null) return false;

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
        if (scope is null) return false;

        sc.IsExcluded = true;
        if (string.IsNullOrWhiteSpace(sc.ExclusionReason))
        {
            sc.ExclusionReason = $"[Accepted suggestion] {sc.AttributionReason}";
        }
        sc.IncludedInReport = false;
        sc.UpdatedUtc = DateTime.UtcNow;

        var targetKind = await ResolveTargetKindAsync(sc.TargetId, ct);

        db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
        {
            ScopeCaseId = sc.ScopeCaseId,
            PreLoginReportJobId = scope.ExternalId,
            CaseRowId = sc.Alias,
            TargetId = sc.TargetId ?? 0,
            TargetKind = targetKind,
            PartyName = Truncate(sc.AttributionParty ?? string.Empty, 500)!,
            ReasonKind = sc.AttributionRule ?? "suggestion-accepted",
            Action = PreLoginAttributionAction.Accepted,
            DecidedBy = decidedBy,
            DecidedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RejectSuggestionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
        if (sc is null) return false;

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
        if (scope is null) return false;

        sc.IsExcluded = false;
        sc.IncludedInReport = true;
        sc.UpdatedUtc = DateTime.UtcNow;

        var targetKind = await ResolveTargetKindAsync(sc.TargetId, ct);

        db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
        {
            ScopeCaseId = sc.ScopeCaseId,
            PreLoginReportJobId = scope.ExternalId,
            CaseRowId = sc.Alias,
            TargetId = sc.TargetId ?? 0,
            TargetKind = targetKind,
            PartyName = Truncate(sc.AttributionParty ?? string.Empty, 500)!,
            ReasonKind = sc.AttributionRule ?? "suggestion-rejected",
            Action = PreLoginAttributionAction.Rejected,
            DecidedBy = decidedBy,
            DecidedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> UndoExclusionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
        if (sc is null) return false;

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
        if (scope is null) return false;

        sc.IsExcluded = false;
        sc.IncludedInReport = true;
        sc.UpdatedUtc = DateTime.UtcNow;

        var targetKind = await ResolveTargetKindAsync(sc.TargetId, ct);

        db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
        {
            ScopeCaseId = sc.ScopeCaseId,
            PreLoginReportJobId = scope.ExternalId,
            CaseRowId = sc.Alias,
            TargetId = sc.TargetId ?? 0,
            TargetKind = targetKind,
            PartyName = Truncate(sc.AttributionParty ?? string.Empty, 500)!,
            ReasonKind = sc.AttributionRule ?? "auto-exclusion-undone",
            Action = PreLoginAttributionAction.Undone,
            DecidedBy = decidedBy,
            DecidedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task RecordAnalystMissAsync(long scopeCaseId, string decidedBy = "Analyst", string? reason = null, CancellationToken ct = default)
    {
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
        if (sc is null) return;

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
        if (scope is null) return;

        var targetKind = await ResolveTargetKindAsync(sc.TargetId, ct);

        db.PreLoginAttributionDecisions.Add(new PreLoginAttributionDecision
        {
            ScopeCaseId = sc.ScopeCaseId,
            PreLoginReportJobId = scope.ExternalId,
            CaseRowId = sc.Alias,
            TargetId = sc.TargetId ?? 0,
            TargetKind = targetKind,
            PartyName = Truncate(sc.AttributionParty ?? string.Empty, 500)!,
            ReasonKind = sc.AttributionRule ?? reason ?? "analyst-miss",
            Action = PreLoginAttributionAction.AnalystMiss,
            DecidedBy = decidedBy,
            DecidedUtc = DateTime.UtcNow
        });

        await db.SaveChangesAsync(ct);
    }

    private async Task<PreLoginTargetKind> ResolveTargetKindAsync(long? targetId, CancellationToken ct)
    {
        if (!targetId.HasValue || targetId.Value <= 0) return PreLoginTargetKind.Borrower;
        var target = await db.PreLoginLitigationTargets
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.PreLoginLitigationTargetId == targetId.Value, ct);
        return target?.TargetKind ?? PreLoginTargetKind.Borrower;
    }

    public async Task<PreLoginTrustReport> GetTrustReportAsync(long? jobId = null, CancellationToken ct = default)
    {
        var query = db.PreLoginAttributionDecisions.AsNoTracking();
        if (jobId.HasValue) query = query.Where(d => d.PreLoginReportJobId == jobId.Value);

        var decisions = await query.ToListAsync(ct);

        var grouped = decisions.GroupBy(d => d.ReasonKind).OrderBy(g => g.Key);
        var rows = new List<TrustReportRow>();

        int totalAccepted = 0, totalAutoApplied = 0, totalRejected = 0, totalUndone = 0, totalMisses = 0;

        foreach (var g in grouped)
        {
            var suggested = g.Count(d => d.Action == PreLoginAttributionAction.Suggested);
            var accepted = g.Count(d => d.Action == PreLoginAttributionAction.Accepted);
            var rejected = g.Count(d => d.Action == PreLoginAttributionAction.Rejected);
            var autoApplied = g.Count(d => d.Action == PreLoginAttributionAction.AutomaticallyApplied);
            var undone = g.Count(d => d.Action == PreLoginAttributionAction.Undone);
            var miss = g.Count(d => d.Action == PreLoginAttributionAction.AnalystMiss);

            totalAccepted += accepted;
            totalAutoApplied += autoApplied;
            totalRejected += rejected;
            totalUndone += undone;
            totalMisses += miss;

            var correct = accepted + (autoApplied - undone);
            var denominator = accepted + autoApplied + rejected;
            var precision = denominator > 0 ? (double)Math.Max(0, correct) / denominator : 1.0;

            rows.Add(new TrustReportRow(g.Key, suggested, accepted, rejected, autoApplied, undone, miss, Math.Round(precision, 4)));
        }

        var totalCorrect = totalAccepted + (totalAutoApplied - totalUndone);
        var totalDenominator = totalAccepted + totalAutoApplied + totalRejected;
        var overallPrecision = totalDenominator > 0 ? (double)Math.Max(0, totalCorrect) / totalDenominator : 1.0;

        return new PreLoginTrustReport(jobId, rows, decisions.Count, Math.Round(overallPrecision, 4));
    }

    private async Task<Dictionary<string, (LitigationPartyVerdictKind Verdict, string? Reason, string PartyName)>> JudgePartiesAsync(
        long jobId, string companyName, string? cin, string subjectKey, IReadOnlyList<string> parties, CancellationToken ct)
    {
        var result = new Dictionary<string, (LitigationPartyVerdictKind, string?, string)>(StringComparer.Ordinal);
        ILitigationAiAnalysisClient? client;
        try { client = services.GetService<ILitigationAiAnalysisClient>(); }
        catch { client = null; }

        if (client is null)
        {
            logger?.LogWarning("Litigation attribution for pre-login job {JobId}: AI client not available.", jobId);
            return result;
        }

        var subject = new AttributionSubject(companyName, [], cin, null);
        foreach (var batch in parties.Chunk(PartyAttributionJudge.BatchSize))
        {
            LitigationAiCallResult call;
            using (AiUsageContext.Begin(jobId, AiUsagePurposes.LitigationAttribution))
            {
                call = await client.CallAsync(PartyAttributionJudge.BuildPrompt(subject, batch), TimeoutSeconds, ct);
            }

            if (!call.Success)
            {
                logger?.LogWarning("Attribution judgement for pre-login job {JobId} failed: {Reason}", jobId, call.FailureReason);
                continue;
            }

            foreach (var j in PartyAttributionJudge.Read(batch, call.RawResponse))
            {
                var key = LitigationAttributionService.PartyKey(j.Party);
                if (key.Length == 0) continue;
                result[key] = (j.Verdict, j.Reason, j.Party);

                db.LitigationPartyVerdicts.Add(new LitigationPartyVerdict
                {
                    SubjectKey = subjectKey,
                    PartyKey = Truncate(key, 400)!,
                    PartyName = Truncate(j.Party, 500)!,
                    PromptVersion = PartyAttributionJudge.PromptVersion,
                    Model = VertexLitigationAiAnalysisClient.ModelId,
                    Verdict = j.Verdict,
                    Reason = Truncate(j.Reason, 600),
                    CreatedUtc = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    private static IReadOnlyList<string> ExtractParties(LegalCaseRecord? c)
    {
        if (c is null) return [];
        var list = new List<string>();

        void AddNames(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text == "-") return;
            var parts = text.Split(new[] { ", ,", ",", "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
            {
                var trimmed = p.Trim();
                if (trimmed.Length > 0 && !trimmed.Equals("-", StringComparison.Ordinal) && !trimmed.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(trimmed);
                }
            }
        }

        AddNames(c.Petitioner);
        AddNames(c.Respondent);

        if (list.Count == 0 && !string.IsNullOrWhiteSpace(c.CaseDetails) && c.CaseDetails != "-")
        {
            var vsIndex = c.CaseDetails.IndexOf(" VS ", StringComparison.OrdinalIgnoreCase);
            if (vsIndex > 0)
            {
                AddNames(c.CaseDetails[..vsIndex]);
                AddNames(c.CaseDetails[(vsIndex + 4)..]);
            }
            else
            {
                AddNames(c.CaseDetails);
            }
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string SubjectKey(string? cin, string companyName)
    {
        if (!string.IsNullOrWhiteSpace(cin)) return cin.Trim().ToUpperInvariant();
        return "NAME:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(companyName.Trim().ToUpperInvariant())))[..40];
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}

