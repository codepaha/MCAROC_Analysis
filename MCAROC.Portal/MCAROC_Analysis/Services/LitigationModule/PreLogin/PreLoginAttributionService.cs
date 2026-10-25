using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.AiUsage;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public sealed record PreLoginAttributionRunResult(
    int TotalCases, int Assessed, int Attributed, int NotTheCompany, int Ambiguous, int SuggestedExclusions, int AutoExcluded);

public sealed record TrustReportRow(
    string ReasonKind, int Suggested, int Accepted, int Rejected, int AutoApplied, int Undone, int AnalystMiss, double? Precision);

public sealed record TrustReportTargetRow(
    PreLoginTargetKind TargetKind, int Suggested, int Accepted, int Rejected, int AutoApplied, int Undone, int AnalystMiss, double? Precision);

public sealed record PreLoginTrustReport(
    long? JobId,
    DateTime? FromUtc,
    DateTime? ToUtc,
    IReadOnlyList<TrustReportRow> Rows,
    IReadOnlyList<TrustReportTargetRow> TargetRows,
    int TotalDecisions,
    int TotalSuggested,
    int TotalAccepted,
    int TotalRejected,
    int TotalAutoApplied,
    int TotalUndone,
    int TotalMisses,
    double? OverallPrecision);

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
    ILogger<PreLoginAttributionService>? logger = null,
    PreLoginReportService? reports = null,
    IWebHostEnvironment? environment = null)
{
    private const int TimeoutSeconds = 120;
    private readonly PreLoginReportService? _reports = reports ?? services.GetService<PreLoginReportService>();
    private readonly IWebHostEnvironment? _environment = environment ?? services.GetService<IWebHostEnvironment>();

    internal Func<Task>? OnAfterConflictCheckAsync { get; set; }

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

        var job = await db.PreLoginReportJobs
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
                // Safe upgrade policy: if AttributionGrade is missing on an already settled row, backfill it without touching decisions or exclusions
                if (sc.AttributionGrade == null)
                {
                    sc.AttributionGrade = sc.AttributionOutcome switch
                    {
                        LitigationAttributionOutcome.Attributed => PersonAttributionGrade.Confirmed,
                        LitigationAttributionOutcome.NotTheCompany => PersonAttributionGrade.Excluded,
                        LitigationAttributionOutcome.Ambiguous => PersonAttributionGrade.Possible,
                        _ => null
                    };
                }
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
                    var companyGrade = assessment.Outcome switch
                    {
                        LitigationAttributionOutcome.Attributed => PersonAttributionGrade.Confirmed,
                        LitigationAttributionOutcome.NotTheCompany => PersonAttributionGrade.Excluded,
                        LitigationAttributionOutcome.Ambiguous => PersonAttributionGrade.Possible,
                        _ => (PersonAttributionGrade?)null
                    };
                    ApplyOutcome(sc, target, assessment.Outcome!.Value, assessment.Rule, assessment.Party, assessment.Reason, mode, now, ref suggested, ref autoExcluded, companyGrade);
                    assessed++;
                    if (assessment.Outcome == LitigationAttributionOutcome.Attributed) attributed++;
                    else if (assessment.Outcome == LitigationAttributionOutcome.NotTheCompany) notCompany++;
                    else ambiguous++;
                }
            }
            else
            {
                // Individual target (Director): rules only until #452
                var profile = GetPersonTargetProfile(job, target, data);
                var personAssessment = PersonAttributionRules.Assess(parties, profile, rec?.District, rec?.State, rec?.CaseType, rec?.Court);
                var (outcome, rule, party, reason) = (personAssessment.Outcome, personAssessment.Rule, personAssessment.Party, personAssessment.Reason);
                var signalsJson = JsonSerializer.Serialize(personAssessment.Signals);
                ApplyOutcome(sc, target, outcome, rule, party, reason, mode, now, ref suggested, ref autoExcluded, personAssessment.Grade, signalsJson);
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
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Attributed, "judged-same", same.Party, same.V.Reason, mode, now, ref suggested, ref autoExcluded, PersonAttributionGrade.Confirmed);
                        attributed++;
                    }
                    else if (found.Count < candidates.Count)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Pending, null, null, "Waiting for party attribution judgement.", mode, now, ref suggested, ref autoExcluded, null);
                        ambiguous++;
                    }
                    else if (found.FirstOrDefault(x => x.V.Verdict == LitigationPartyVerdictKind.Unsure) is { Has: true } unsure)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Ambiguous, "judged-unsure", unsure.Party, unsure.V.Reason, mode, now, ref suggested, ref autoExcluded, PersonAttributionGrade.Possible);
                        ambiguous++;
                    }
                    else if (rules.Truncated)
                    {
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.Ambiguous, "judged-partial", null,
                            $"Checked {candidates.Count} candidate parties without finding target; others were not checked.", mode, now, ref suggested, ref autoExcluded, PersonAttributionGrade.Possible);
                        ambiguous++;
                    }
                    else
                    {
                        var first = found.FirstOrDefault();
                        ApplyOutcome(sc, t, LitigationAttributionOutcome.NotTheCompany, "judged-different", first.Party, first.V.Reason, mode, now, ref suggested, ref autoExcluded, PersonAttributionGrade.Excluded);
                        notCompany++;
                    }
                }
            }
        }

        string? oldReportPath = null;
        string? newReportPath = null;

        if (mode == PreLoginAttributionMode.Apply && autoExcluded > 0)
        {
            var autoExcludedScopeCases = scopeCases.Where(sc => sc.IsExcluded).ToList();
            if (autoExcludedScopeCases.Count > 0)
            {
                var exclusionMap = autoExcludedScopeCases.ToDictionary(sc => sc.Alias, sc => sc.ExclusionReason);
                if (!TrySyncCasesToDataJson(job.DataJson, exclusionMap, isExcludedDefault: true, out var updatedJson, out var updatedData, out var syncError))
                {
                    logger?.LogError("Failed to synchronize {Count} automatic exclusions to DataJson for job {JobId}: {Error}", autoExcludedScopeCases.Count, jobId, syncError);
                    db.ChangeTracker.Clear();
                    throw new InvalidOperationException($"Failed to synchronize automatic exclusions to assignment DataJson for job {jobId}: {syncError}");
                }
                job.DataJson = updatedJson;
                oldReportPath = job.ReportStoragePath;
                if (updatedData != null)
                {
                    newReportPath = await TryPublishReportAsync(job, updatedData, job.DataVersion + 1, ct);
                    if (newReportPath != null)
                    {
                        job.ReportStoragePath = newReportPath;
                    }
                }
                job.DataVersion++;
            }
        }

        await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.SaveChangesAsync(ct);
            if (tx != null) await tx.CommitAsync(ct);

            if (newReportPath != null && !string.IsNullOrWhiteSpace(oldReportPath) && !string.Equals(oldReportPath, newReportPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldReportPath))
            {
                try { File.Delete(oldReportPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            if (newReportPath != null && File.Exists(newReportPath))
            {
                try { File.Delete(newReportPath); } catch { }
            }
            logger?.LogError(ex, "Transaction failed saving attribution results for job {JobId}", jobId);
            if (tx != null) await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            throw;
        }

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
        ref int autoExcluded,
        PersonAttributionGrade? grade = null,
        string? signalsJson = null)
    {
        sc.AttributionOutcome = outcome;
        sc.AttributionRule = rule;
        sc.AttributionParty = Truncate(party, 500);
        sc.AttributionReason = Truncate(reason, 512);
        sc.AttributionGrade = grade;
        sc.AttributionSignalsJson = Truncate(signalsJson, 2000);
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

    private static PersonTargetProfile GetPersonTargetProfile(
        PreLoginReportJob job,
        PreLoginLitigationTarget target,
        InstaReportData? data)
    {
        var targetName = target.TargetName;
        string? din = null;
        string? pan = null;
        string? fatherOrHusband = null;

        var dMatch = data?.Directors?.FirstOrDefault(d => string.Equals(d.Name?.Trim(), targetName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (dMatch != null && !string.IsNullOrWhiteSpace(dMatch.DinOrPan))
        {
            var clean = dMatch.DinOrPan.Trim();
            if (clean.Length == 8 && clean.All(char.IsDigit)) din = clean;
            else if (clean.Length == 10) pan = clean;
        }

        var pMatch = data?.Assignment?.CompanyDetails?.DirectorsOrPartners?.FirstOrDefault(p => string.Equals(p.Name?.Trim(), targetName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pMatch != null)
        {
            din ??= pMatch.Din;
            pan ??= pMatch.Pan;
        }

        var associatedCompany = job.SubmittedCompanyName ?? data?.Company?.Name ?? data?.Assignment?.CompanyDetails?.CompanyName;

        // Individual director address is NOT the borrower company's registered office or RoC.
        // Unless identity-linked personal address evidence exists for the person, personal location is left null
        // so cross-state cases are not falsely excluded by company geography.
        string? personalDistrict = null;
        string? personalState = null;

        return new PersonTargetProfile(
            Name: targetName,
            FatherOrHusbandName: fatherOrHusband,
            District: personalDistrict,
            State: personalState,
            AssociatedCompanyName: associatedCompany,
            Din: din,
            Pan: pan);
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
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            db.ChangeTracker.Clear();

            var scope = await db.Set<ModuleScope>()
                .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

            if (scope is null) return 0;

            var openSuggestions = await db.Set<ModuleScopeCase>()
                .Where(sc => sc.ScopeId == scope.ScopeId &&
                             sc.AttributionOutcome == LitigationAttributionOutcome.NotTheCompany &&
                             !sc.IsExcluded)
                .ToListAsync(ct);

            if (openSuggestions.Count == 0) return 0;

            // Filter out any cases that have prior decisions: Rejected, Accepted, or AutomaticallyApplied
            var candidateIds = openSuggestions.Select(sc => sc.ScopeCaseId).ToList();
            var decidedScopeCaseIds = await db.PreLoginAttributionDecisions.AsNoTracking()
                .Where(d => candidateIds.Contains(d.ScopeCaseId) &&
                            (d.Action == PreLoginAttributionAction.Rejected ||
                             d.Action == PreLoginAttributionAction.Accepted ||
                             d.Action == PreLoginAttributionAction.AutomaticallyApplied))
                .Select(d => d.ScopeCaseId)
                .Distinct()
                .ToListAsync(ct);

            if (decidedScopeCaseIds.Count > 0)
            {
                var decidedSet = decidedScopeCaseIds.ToHashSet();
                openSuggestions.RemoveAll(sc => decidedSet.Contains(sc.ScopeCaseId));
            }

            if (openSuggestions.Count == 0) return 0;

            var job = await db.PreLoginReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.PreLoginReportJobId == jobId, ct);
            if (job is null)
            {
                logger?.LogWarning("Cannot apply open suggestions: Job {JobId} not found", jobId);
                return 0;
            }

            var exclusionMap = openSuggestions.ToDictionary(
                sc => sc.Alias,
                sc => (string?)(string.IsNullOrWhiteSpace(sc.ExclusionReason) ? $"[Auto] {sc.AttributionReason}" : sc.ExclusionReason));

            if (!TrySyncCasesToDataJson(job.DataJson, exclusionMap, isExcludedDefault: true, out var updatedJson, out var updatedData, out var syncError))
            {
                logger?.LogError("Failed to synchronize {Count} open suggestions to DataJson for job {JobId}: {Error}", openSuggestions.Count, jobId, syncError);
                db.ChangeTracker.Clear();
                throw new InvalidOperationException($"Failed to synchronize open suggestions to assignment DataJson for job {jobId}: {syncError}");
            }

            var currentVersion = job.DataVersion;
            var oldReportPath = job.ReportStoragePath;
            string? newReportPath = null;

            if (updatedData != null)
            {
                newReportPath = await TryPublishReportAsync(job, updatedData, currentVersion + 1, ct);
            }

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

            await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Verify no concurrent decisions were made on any of these cases
                var activeIds = openSuggestions.Select(sc => sc.ScopeCaseId).ToList();
                var anyConcurrentDecisions = await db.PreLoginAttributionDecisions
                    .AnyAsync(d => activeIds.Contains(d.ScopeCaseId) &&
                                   (d.Action == PreLoginAttributionAction.Rejected ||
                                    d.Action == PreLoginAttributionAction.Accepted ||
                                    d.Action == PreLoginAttributionAction.AutomaticallyApplied), ct);

                if (anyConcurrentDecisions)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Concurrency conflict: one or more suggestions in job {JobId} were decided concurrently. Attempt {Attempt} of {MaxRetries}.", jobId, attempt + 1, maxRetries);
                    await Task.Delay(50 * (attempt + 1), ct);
                    continue;
                }

                if (OnAfterConflictCheckAsync != null)
                {
                    await OnAfterConflictCheckAsync();
                }

                await db.SaveChangesAsync(ct);

                var rowsAffected = await db.PreLoginReportJobs
                    .Where(j => j.PreLoginReportJobId == jobId && j.DataVersion == currentVersion)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.DataJson, updatedJson)
                        .SetProperty(x => x.ReportStoragePath, x => newReportPath ?? x.ReportStoragePath)
                        .SetProperty(x => x.DataVersion, currentVersion + 1), ct);

                if (rowsAffected == 0)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Concurrency conflict applying open suggestions for job {JobId}. Attempt {Attempt} of {MaxRetries}.", jobId, attempt + 1, maxRetries);
                    await Task.Delay(50 * (attempt + 1), ct);
                    continue;
                }

                if (tx != null) await tx.CommitAsync(ct);

                if (!string.IsNullOrWhiteSpace(oldReportPath) && !string.Equals(oldReportPath, newReportPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldReportPath))
                {
                    try { File.Delete(oldReportPath); } catch { }
                }

                return openSuggestions.Count;
            }
            catch (Exception ex)
            {
                if (newReportPath != null && File.Exists(newReportPath))
                {
                    try { File.Delete(newReportPath); } catch { }
                }
                logger?.LogError(ex, "Transaction failed saving open suggestions for job {JobId}", jobId);
                if (tx != null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        throw new DbUpdateConcurrencyException($"Failed to apply open suggestions for job {jobId} due to persistent concurrency conflicts.");
    }

    public Task<bool> AcceptSuggestionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
        => AcceptSuggestionAsync(scopeCaseId, decidedBy, autoSave: true, ct);

    public async Task<bool> AcceptSuggestionAsync(long scopeCaseId, string decidedBy, bool autoSave, CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            db.ChangeTracker.Clear();

            var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
            if (sc is null) return false;

            // Must be an open suggestion: outcome must be NotTheCompany and not already excluded
            if (sc.IsExcluded || sc.AttributionOutcome != LitigationAttributionOutcome.NotTheCompany)
            {
                logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Not an open suggestion (IsExcluded={IsExcluded}, Outcome={Outcome}).",
                    scopeCaseId, sc.IsExcluded, sc.AttributionOutcome);
                return false;
            }

            var lastDecision = await db.PreLoginAttributionDecisions.AsNoTracking()
                .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
                .OrderByDescending(d => d.DecidedUtc)
                .FirstOrDefaultAsync(ct);

            if (lastDecision != null &&
                (lastDecision.Action == PreLoginAttributionAction.Rejected ||
                 lastDecision.Action == PreLoginAttributionAction.Accepted ||
                 lastDecision.Action == PreLoginAttributionAction.AutomaticallyApplied))
            {
                logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Existing decision {Action} recorded.", scopeCaseId, lastDecision.Action);
                return false;
            }

            var scope = await db.Set<ModuleScope>().AsNoTracking().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
            if (scope is null) return false;

            var job = await db.PreLoginReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.PreLoginReportJobId == scope.ExternalId, ct);
            if (job is null) return false;

            var currentVersion = job.DataVersion;

            await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
            try
            {
                var hasConflictingDecision = await db.PreLoginAttributionDecisions
                    .AnyAsync(d => d.ScopeCaseId == sc.ScopeCaseId &&
                                   (d.Action == PreLoginAttributionAction.Rejected ||
                                    d.Action == PreLoginAttributionAction.Accepted ||
                                    d.Action == PreLoginAttributionAction.AutomaticallyApplied), ct);

                if (hasConflictingDecision)
                {
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Conflicting decision committed concurrently.", scopeCaseId);
                    return false;
                }

                if (OnAfterConflictCheckAsync != null)
                {
                    await OnAfterConflictCheckAsync();
                }

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

                if (autoSave)
                {
                    await db.SaveChangesAsync(ct);

                    var rowsAffected = await db.PreLoginReportJobs
                        .Where(j => j.PreLoginReportJobId == job.PreLoginReportJobId && j.DataVersion == currentVersion)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.DataVersion, currentVersion + 1), ct);

                    if (rowsAffected == 0)
                    {
                        if (tx != null) await tx.RollbackAsync(ct);
                        db.ChangeTracker.Clear();
                        logger?.LogWarning("Concurrency conflict syncing accepted suggestion for job {JobId}. Attempt {Attempt} of {MaxRetries}.", job.PreLoginReportJobId, attempt + 1, maxRetries);
                        await Task.Delay(50 * (attempt + 1), ct);
                        continue;
                    }

                    if (tx != null) await tx.CommitAsync(ct);
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Transaction failed saving accepted suggestion {ScopeCaseId} for job {JobId}", scopeCaseId, job.PreLoginReportJobId);
                if (tx != null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        logger?.LogError("Exceeded max retries accepting suggestion {ScopeCaseId} due to concurrency conflicts.", scopeCaseId);
        return false;
    }

    public Task<bool> RejectSuggestionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
        => RejectSuggestionAsync(scopeCaseId, decidedBy, autoSave: true, ct);

    public async Task<bool> RejectSuggestionAsync(long scopeCaseId, string decidedBy, bool autoSave, CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            db.ChangeTracker.Clear();

            var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
            if (sc is null) return false;

            var lastDecision = await db.PreLoginAttributionDecisions.AsNoTracking()
                .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
                .OrderByDescending(d => d.DecidedUtc)
                .FirstOrDefaultAsync(ct);

            if (lastDecision != null &&
                (lastDecision.Action == PreLoginAttributionAction.Rejected ||
                 lastDecision.Action == PreLoginAttributionAction.Accepted ||
                 lastDecision.Action == PreLoginAttributionAction.AutomaticallyApplied))
            {
                logger?.LogWarning("Cannot reject suggestion for ScopeCaseId {ScopeCaseId}: Already decided ({Action}).", scopeCaseId, lastDecision.Action);
                return false;
            }

            var scope = await db.Set<ModuleScope>().AsNoTracking().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
            if (scope is null) return false;

            var job = await db.PreLoginReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.PreLoginReportJobId == scope.ExternalId, ct);
            if (job is null) return false;

            var currentVersion = job.DataVersion;

            await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
            try
            {
                var hasConflictingDecision = await db.PreLoginAttributionDecisions
                    .AnyAsync(d => d.ScopeCaseId == sc.ScopeCaseId &&
                                   (d.Action == PreLoginAttributionAction.Rejected ||
                                    d.Action == PreLoginAttributionAction.Accepted ||
                                    d.Action == PreLoginAttributionAction.AutomaticallyApplied), ct);

                if (hasConflictingDecision)
                {
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Cannot reject suggestion for ScopeCaseId {ScopeCaseId}: Conflicting decision committed concurrently.", scopeCaseId);
                    return false;
                }

                if (OnAfterConflictCheckAsync != null)
                {
                    await OnAfterConflictCheckAsync();
                }

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

                if (autoSave)
                {
                    await db.SaveChangesAsync(ct);

                    var rowsAffected = await db.PreLoginReportJobs
                        .Where(j => j.PreLoginReportJobId == job.PreLoginReportJobId && j.DataVersion == currentVersion)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(x => x.DataVersion, currentVersion + 1), ct);

                    if (rowsAffected == 0)
                    {
                        if (tx != null) await tx.RollbackAsync(ct);
                        db.ChangeTracker.Clear();
                        logger?.LogWarning("Concurrency conflict syncing rejected suggestion for job {JobId}. Attempt {Attempt} of {MaxRetries}.", job.PreLoginReportJobId, attempt + 1, maxRetries);
                        await Task.Delay(50 * (attempt + 1), ct);
                        continue;
                    }

                    if (tx != null) await tx.CommitAsync(ct);
                }

                return true;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Transaction failed saving rejected suggestion {ScopeCaseId} for job {JobId}", scopeCaseId, job.PreLoginReportJobId);
                if (tx != null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        logger?.LogError("Exceeded max retries rejecting suggestion {ScopeCaseId} due to concurrency conflicts.", scopeCaseId);
        return false;
    }

    public Task<bool> UndoExclusionAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
        => UndoExclusionAsync(scopeCaseId, decidedBy, autoSave: true, ct);

    public async Task<bool> UndoExclusionAsync(long scopeCaseId, string decidedBy, bool autoSave, CancellationToken ct = default)
    {
        var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
        if (sc is null) return false;

        var scope = await db.Set<ModuleScope>().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
        if (scope is null) return false;

        var lastDecision = await db.PreLoginAttributionDecisions.AsNoTracking()
            .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
            .OrderByDescending(d => d.DecidedUtc)
            .FirstOrDefaultAsync(ct);

        if (lastDecision is null || lastDecision.Action != PreLoginAttributionAction.AutomaticallyApplied)
        {
            logger?.LogWarning("Cannot undo exclusion for ScopeCaseId {ScopeCaseId}: Not automatically applied.", scopeCaseId);
            return false;
        }

        sc.IsExcluded = false;
        sc.IncludedInReport = true;
        sc.ExclusionReason = null;
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

        if (autoSave)
        {
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public async Task<IReadOnlyDictionary<string, PreLoginCaseAttributionDto>> GetCaseAttributionsForJobAsync(long jobId, CancellationToken ct = default)
    {
        var scope = await db.Set<ModuleScope>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Kind == ModuleScopeKind.Assignment && s.ExternalId == jobId, ct);

        if (scope is null) return new Dictionary<string, PreLoginCaseAttributionDto>(StringComparer.OrdinalIgnoreCase);

        var scopeCases = await db.Set<ModuleScopeCase>().AsNoTracking()
            .Where(sc => sc.ScopeId == scope.ScopeId)
            .ToListAsync(ct);

        var targets = await db.PreLoginLitigationTargets.AsNoTracking()
            .Where(t => t.PreLoginReportJobId == jobId)
            .ToDictionaryAsync(t => t.PreLoginLitigationTargetId, ct);

        var decisions = await db.PreLoginAttributionDecisions.AsNoTracking()
            .Where(d => d.PreLoginReportJobId == jobId)
            .GroupBy(d => d.ScopeCaseId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderByDescending(d => d.DecidedUtc).First(), ct);

        var dict = new Dictionary<string, PreLoginCaseAttributionDto>(StringComparer.OrdinalIgnoreCase);

        foreach (var sc in scopeCases)
        {
            var target = sc.TargetId.HasValue && targets.TryGetValue(sc.TargetId.Value, out var t) ? t : null;
            var targetKind = target?.TargetKind ?? PreLoginTargetKind.Borrower;
            var targetName = target?.TargetName;

            decisions.TryGetValue(sc.ScopeCaseId, out var lastDecision);

            bool isSuggestedExclusion = sc.AttributionOutcome == LitigationAttributionOutcome.NotTheCompany &&
                                        !sc.IsExcluded &&
                                        lastDecision?.Action != PreLoginAttributionAction.Rejected;

            bool isAutoExcluded = sc.IsExcluded &&
                                  lastDecision?.Action == PreLoginAttributionAction.AutomaticallyApplied;

            bool isUnconfirmed = sc.AttributionOutcome == LitigationAttributionOutcome.Ambiguous;

            var effectiveGrade = sc.AttributionGrade ?? (sc.AttributionOutcome switch
            {
                LitigationAttributionOutcome.Attributed => PersonAttributionGrade.Confirmed,
                LitigationAttributionOutcome.NotTheCompany => PersonAttributionGrade.Excluded,
                LitigationAttributionOutcome.Ambiguous => PersonAttributionGrade.Possible,
                _ => (PersonAttributionGrade?)null
            });

            dict[sc.Alias] = new PreLoginCaseAttributionDto(
                sc.ScopeCaseId,
                sc.Alias,
                sc.TargetId,
                targetName,
                targetKind,
                sc.AttributionOutcome,
                sc.AttributionRule,
                sc.AttributionParty,
                sc.AttributionReason,
                sc.IsExcluded,
                sc.ExclusionReason,
                isSuggestedExclusion,
                isAutoExcluded,
                isUnconfirmed,
                effectiveGrade,
                sc.AttributionSignalsJson
            );
        }

        return dict;
    }

    public async Task<IReadOnlyList<PreLoginSuggestionItemDto>> GetOpenSuggestionsAsync(
        long? jobId = null,
        string? reasonKind = null,
        PreLoginTargetKind? targetKind = null,
        CancellationToken ct = default)
    {
        var baseQuery = from sc in db.Set<ModuleScopeCase>().AsNoTracking()
                        join s in db.Set<ModuleScope>().AsNoTracking() on sc.ScopeId equals s.ScopeId
                        where s.Kind == ModuleScopeKind.Assignment &&
                              sc.AttributionOutcome == LitigationAttributionOutcome.NotTheCompany &&
                              !sc.IsExcluded
                        select new { Case = sc, Scope = s };

        if (jobId.HasValue)
        {
            baseQuery = baseQuery.Where(x => x.Scope.ExternalId == jobId.Value);
        }

        if (!string.IsNullOrWhiteSpace(reasonKind))
        {
            baseQuery = baseQuery.Where(x => x.Case.AttributionRule == reasonKind);
        }

        var scopeItems = await baseQuery.ToListAsync(ct);
        if (scopeItems.Count == 0) return [];

        var jobIds = scopeItems.Select(x => x.Scope.ExternalId).Distinct().ToList();

        var jobs = await db.PreLoginReportJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.PreLoginReportJobId))
            .ToDictionaryAsync(j => j.PreLoginReportJobId, ct);

        var targets = await db.PreLoginLitigationTargets.AsNoTracking()
            .Where(t => jobIds.Contains(t.PreLoginReportJobId))
            .ToDictionaryAsync(t => t.PreLoginLitigationTargetId, ct);

        var scopeCaseIds = scopeItems.Select(x => x.Case.ScopeCaseId).ToList();
        var decisions = await db.PreLoginAttributionDecisions.AsNoTracking()
            .Where(d => scopeCaseIds.Contains(d.ScopeCaseId))
            .GroupBy(d => d.ScopeCaseId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderByDescending(d => d.DecidedUtc).First(), ct);

        // Deserialize each job's DataJson once into a case map
        var caseMapByJob = new Dictionary<long, Dictionary<string, LegalCaseRecord>>();
        foreach (var job in jobs.Values)
        {
            var map = new Dictionary<string, LegalCaseRecord>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(job.DataJson))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson);
                    if (data?.LegalCases?.Cases != null)
                    {
                        foreach (var c in data.LegalCases.Cases)
                        {
                            if (!string.IsNullOrWhiteSpace(c.CaseRowId))
                            {
                                map[c.CaseRowId] = c;
                            }
                        }
                    }
                }
                catch { }
            }
            caseMapByJob[job.PreLoginReportJobId] = map;
        }

        var results = new List<PreLoginSuggestionItemDto>();

        foreach (var item in scopeItems)
        {
            var sc = item.Case;
            var scope = item.Scope;
            if (!jobs.TryGetValue(scope.ExternalId, out var job)) continue;

            if (decisions.TryGetValue(sc.ScopeCaseId, out var lastDecision))
            {
                if (lastDecision.Action == PreLoginAttributionAction.Rejected) continue;
            }

            var target = sc.TargetId.HasValue && targets.TryGetValue(sc.TargetId.Value, out var t) ? t : null;
            var actualTargetKind = target?.TargetKind ?? PreLoginTargetKind.Borrower;
            var actualTargetName = target?.TargetName ?? job.SubmittedCompanyName ?? job.Cin;

            if (targetKind.HasValue && actualTargetKind != targetKind.Value) continue;

            caseMapByJob.TryGetValue(job.PreLoginReportJobId, out var jobCases);
            LegalCaseRecord? caseRecord = null;
            jobCases?.TryGetValue(sc.Alias, out caseRecord);

            var suggestedUtc = decisions.TryGetValue(sc.ScopeCaseId, out var d) ? d.DecidedUtc : (sc.UpdatedUtc ?? sc.CreatedUtc);

            results.Add(new PreLoginSuggestionItemDto(
                sc.ScopeCaseId,
                job.PreLoginReportJobId,
                job.SubmittedCompanyName ?? job.Cin,
                job.Cin,
                job.BatchId,
                sc.Alias,
                sc.TargetId,
                actualTargetName,
                actualTargetKind,
                caseRecord?.Court,
                caseRecord?.CaseNo,
                caseRecord?.Category,
                sc.AttributionRule ?? "rule",
                sc.AttributionParty,
                sc.AttributionReason ?? "Suggested exclusion",
                suggestedUtc
            ));
        }

        return results.OrderByDescending(r => r.SuggestedUtc).ToList();
    }

    public async Task<IReadOnlyList<PreLoginAutoExcludedItemDto>> GetAutoExcludedCasesAsync(
        long? jobId = null,
        string? reasonKind = null,
        PreLoginTargetKind? targetKind = null,
        CancellationToken ct = default)
    {
        var baseQuery = from sc in db.Set<ModuleScopeCase>().AsNoTracking()
                        join s in db.Set<ModuleScope>().AsNoTracking() on sc.ScopeId equals s.ScopeId
                        where s.Kind == ModuleScopeKind.Assignment && sc.IsExcluded
                        select new { Case = sc, Scope = s };

        if (jobId.HasValue)
        {
            baseQuery = baseQuery.Where(x => x.Scope.ExternalId == jobId.Value);
        }

        if (!string.IsNullOrWhiteSpace(reasonKind))
        {
            baseQuery = baseQuery.Where(x => x.Case.AttributionRule == reasonKind);
        }

        var scopeItems = await baseQuery.ToListAsync(ct);
        if (scopeItems.Count == 0) return [];

        var jobIds = scopeItems.Select(x => x.Scope.ExternalId).Distinct().ToList();

        var jobs = await db.PreLoginReportJobs.AsNoTracking()
            .Where(j => jobIds.Contains(j.PreLoginReportJobId))
            .ToDictionaryAsync(j => j.PreLoginReportJobId, ct);

        var targets = await db.PreLoginLitigationTargets.AsNoTracking()
            .Where(t => jobIds.Contains(t.PreLoginReportJobId))
            .ToDictionaryAsync(t => t.PreLoginLitigationTargetId, ct);

        var scopeCaseIds = scopeItems.Select(x => x.Case.ScopeCaseId).ToList();
        var decisions = await db.PreLoginAttributionDecisions.AsNoTracking()
            .Where(d => scopeCaseIds.Contains(d.ScopeCaseId))
            .GroupBy(d => d.ScopeCaseId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderByDescending(d => d.DecidedUtc).First(), ct);

        // Deserialize each job's DataJson once into a case map
        var caseMapByJob = new Dictionary<long, Dictionary<string, LegalCaseRecord>>();
        foreach (var job in jobs.Values)
        {
            var map = new Dictionary<string, LegalCaseRecord>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(job.DataJson))
            {
                try
                {
                    var data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson);
                    if (data?.LegalCases?.Cases != null)
                    {
                        foreach (var c in data.LegalCases.Cases)
                        {
                            if (!string.IsNullOrWhiteSpace(c.CaseRowId))
                            {
                                map[c.CaseRowId] = c;
                            }
                        }
                    }
                }
                catch { }
            }
            caseMapByJob[job.PreLoginReportJobId] = map;
        }

        var results = new List<PreLoginAutoExcludedItemDto>();

        foreach (var item in scopeItems)
        {
            var sc = item.Case;
            var scope = item.Scope;
            if (!jobs.TryGetValue(scope.ExternalId, out var job)) continue;

            if (!decisions.TryGetValue(sc.ScopeCaseId, out var lastDecision) ||
                lastDecision.Action != PreLoginAttributionAction.AutomaticallyApplied)
            {
                continue;
            }

            var target = sc.TargetId.HasValue && targets.TryGetValue(sc.TargetId.Value, out var t) ? t : null;
            var actualTargetKind = target?.TargetKind ?? PreLoginTargetKind.Borrower;
            var actualTargetName = target?.TargetName ?? job.SubmittedCompanyName ?? job.Cin;

            if (targetKind.HasValue && actualTargetKind != targetKind.Value) continue;

            caseMapByJob.TryGetValue(job.PreLoginReportJobId, out var jobCases);
            LegalCaseRecord? caseRecord = null;
            jobCases?.TryGetValue(sc.Alias, out caseRecord);

            results.Add(new PreLoginAutoExcludedItemDto(
                sc.ScopeCaseId,
                job.PreLoginReportJobId,
                job.SubmittedCompanyName ?? job.Cin,
                job.Cin,
                job.BatchId,
                sc.Alias,
                sc.TargetId,
                actualTargetName,
                actualTargetKind,
                caseRecord?.Court,
                caseRecord?.CaseNo,
                caseRecord?.Category,
                sc.AttributionRule ?? "rule",
                sc.AttributionParty,
                sc.AttributionReason ?? "Auto-excluded",
                sc.ExclusionReason ?? "Excluded automatically",
                lastDecision.DecidedUtc
            ));
        }

        return results.OrderByDescending(r => r.ExcludedUtc).ToList();
    }

    public async Task<bool> AcceptSuggestionWithSyncAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            db.ChangeTracker.Clear();

            var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
            if (sc is null) return false;

            // Must be an open suggestion: outcome must be NotTheCompany and not already excluded
            if (sc.IsExcluded || sc.AttributionOutcome != LitigationAttributionOutcome.NotTheCompany)
            {
                logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Not an open suggestion (IsExcluded={IsExcluded}, Outcome={Outcome}).",
                    scopeCaseId, sc.IsExcluded, sc.AttributionOutcome);
                return false;
            }

            var lastDecision = await db.PreLoginAttributionDecisions.AsNoTracking()
                .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
                .OrderByDescending(d => d.DecidedUtc)
                .FirstOrDefaultAsync(ct);

            if (lastDecision != null &&
                (lastDecision.Action == PreLoginAttributionAction.Rejected ||
                 lastDecision.Action == PreLoginAttributionAction.Accepted ||
                 lastDecision.Action == PreLoginAttributionAction.AutomaticallyApplied))
            {
                logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Existing decision {Action} recorded.", scopeCaseId, lastDecision.Action);
                return false;
            }

            var scope = await db.Set<ModuleScope>().AsNoTracking().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
            if (scope is null) return false;

            var job = await db.PreLoginReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.PreLoginReportJobId == scope.ExternalId, ct);
            if (job is null) return false;

            var reason = $"[Accepted suggestion] {sc.AttributionReason}";
            var exclusionMap = new Dictionary<string, string?> { [sc.Alias] = reason };

            if (!TrySyncCasesToDataJson(job.DataJson, exclusionMap, isExcludedDefault: true, out var updatedJson, out var updatedData, out var syncError))
            {
                logger?.LogError("Failed to synchronize accepted suggestion {ScopeCaseId} to DataJson for job {JobId}: {Error}", scopeCaseId, job.PreLoginReportJobId, syncError);
                return false;
            }

            var currentVersion = job.DataVersion;
            var oldReportPath = job.ReportStoragePath;
            string? newReportPath = null;

            if (updatedData != null)
            {
                newReportPath = await TryPublishReportAsync(job, updatedData, currentVersion + 1, ct);
            }

            await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Re-verify in transaction that no conflicting decision was made
                var hasConflictingDecision = await db.PreLoginAttributionDecisions
                    .AnyAsync(d => d.ScopeCaseId == sc.ScopeCaseId &&
                                   (d.Action == PreLoginAttributionAction.Rejected ||
                                    d.Action == PreLoginAttributionAction.Accepted ||
                                    d.Action == PreLoginAttributionAction.AutomaticallyApplied), ct);

                if (hasConflictingDecision)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Cannot accept suggestion for ScopeCaseId {ScopeCaseId}: Conflicting decision committed concurrently.", scopeCaseId);
                    return false;
                }

                if (OnAfterConflictCheckAsync != null)
                {
                    await OnAfterConflictCheckAsync();
                }

                sc.IsExcluded = true;
                if (string.IsNullOrWhiteSpace(sc.ExclusionReason))
                {
                    sc.ExclusionReason = reason;
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

                var rowsAffected = await db.PreLoginReportJobs
                    .Where(j => j.PreLoginReportJobId == job.PreLoginReportJobId && j.DataVersion == currentVersion)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.DataJson, updatedJson)
                        .SetProperty(x => x.ReportStoragePath, x => newReportPath ?? x.ReportStoragePath)
                        .SetProperty(x => x.DataVersion, currentVersion + 1), ct);

                if (rowsAffected == 0)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Concurrency conflict syncing accepted suggestion for job {JobId}. Attempt {Attempt} of {MaxRetries}.", job.PreLoginReportJobId, attempt + 1, maxRetries);
                    await Task.Delay(50 * (attempt + 1), ct);
                    continue;
                }

                if (tx != null) await tx.CommitAsync(ct);

                if (!string.IsNullOrWhiteSpace(oldReportPath) && !string.Equals(oldReportPath, newReportPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldReportPath))
                {
                    try { File.Delete(oldReportPath); } catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                if (newReportPath != null && File.Exists(newReportPath))
                {
                    try { File.Delete(newReportPath); } catch { }
                }
                logger?.LogError(ex, "Transaction failed saving accepted suggestion {ScopeCaseId} for job {JobId}", scopeCaseId, job.PreLoginReportJobId);
                if (tx != null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        logger?.LogError("Exceeded max retries syncing accepted suggestion {ScopeCaseId} due to concurrency conflicts.", scopeCaseId);
        return false;
    }

    public Task<bool> RejectSuggestionWithSyncAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        // Rejecting an exclusion suggestion records the analyst's decision in the audit log
        // without altering the case's exclusion status or modifying DataJson.
        return RejectSuggestionAsync(scopeCaseId, decidedBy, autoSave: true, ct);
    }

    public async Task<bool> UndoExclusionWithSyncAsync(long scopeCaseId, string decidedBy = "Analyst", CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            db.ChangeTracker.Clear();

            var sc = await db.Set<ModuleScopeCase>().FirstOrDefaultAsync(x => x.ScopeCaseId == scopeCaseId, ct);
            if (sc is null) return false;

            if (!sc.IsExcluded)
            {
                logger?.LogWarning("Cannot undo exclusion for ScopeCaseId {ScopeCaseId}: Case is not excluded.", scopeCaseId);
                return false;
            }

            var scope = await db.Set<ModuleScope>().AsNoTracking().FirstOrDefaultAsync(s => s.ScopeId == sc.ScopeId, ct);
            if (scope is null) return false;

            // Only undo if the last decision was AutomaticallyApplied!
            var lastDecision = await db.PreLoginAttributionDecisions.AsNoTracking()
                .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
                .OrderByDescending(d => d.DecidedUtc)
                .FirstOrDefaultAsync(ct);

            if (lastDecision is null || lastDecision.Action != PreLoginAttributionAction.AutomaticallyApplied)
            {
                logger?.LogWarning("Cannot undo exclusion for ScopeCaseId {ScopeCaseId}: Last decision was not AutomaticallyApplied.", scopeCaseId);
                return false;
            }

            var job = await db.PreLoginReportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.PreLoginReportJobId == scope.ExternalId, ct);
            if (job is null) return false;

            var exclusionMap = new Dictionary<string, string?> { [sc.Alias] = null };

            if (!TrySyncCasesToDataJson(job.DataJson, exclusionMap, isExcludedDefault: false, out var updatedJson, out var updatedData, out var syncError))
            {
                logger?.LogError("Failed to synchronize undone exclusion {ScopeCaseId} to DataJson for job {JobId}: {Error}", scopeCaseId, job.PreLoginReportJobId, syncError);
                return false;
            }

            var currentVersion = job.DataVersion;
            var oldReportPath = job.ReportStoragePath;
            string? newReportPath = null;

            if (updatedData != null)
            {
                newReportPath = await TryPublishReportAsync(job, updatedData, currentVersion + 1, ct);
            }

            await using var tx = db.Database.CurrentTransaction != null ? null : await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Re-verify that last decision is still AutomaticallyApplied
                var currentLastDecision = await db.PreLoginAttributionDecisions
                    .Where(d => d.ScopeCaseId == sc.ScopeCaseId)
                    .OrderByDescending(d => d.DecidedUtc)
                    .FirstOrDefaultAsync(ct);

                if (currentLastDecision is null || currentLastDecision.Action != PreLoginAttributionAction.AutomaticallyApplied)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    return false;
                }

                if (OnAfterConflictCheckAsync != null)
                {
                    await OnAfterConflictCheckAsync();
                }

                sc.IsExcluded = false;
                sc.IncludedInReport = true;
                sc.ExclusionReason = null;
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

                var rowsAffected = await db.PreLoginReportJobs
                    .Where(j => j.PreLoginReportJobId == job.PreLoginReportJobId && j.DataVersion == currentVersion)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.DataJson, updatedJson)
                        .SetProperty(x => x.ReportStoragePath, x => newReportPath ?? x.ReportStoragePath)
                        .SetProperty(x => x.DataVersion, currentVersion + 1), ct);

                if (rowsAffected == 0)
                {
                    if (newReportPath != null && File.Exists(newReportPath))
                    {
                        try { File.Delete(newReportPath); } catch { }
                    }
                    if (tx != null) await tx.RollbackAsync(ct);
                    db.ChangeTracker.Clear();
                    logger?.LogWarning("Concurrency conflict syncing undone exclusion for job {JobId}. Attempt {Attempt} of {MaxRetries}.", job.PreLoginReportJobId, attempt + 1, maxRetries);
                    await Task.Delay(50 * (attempt + 1), ct);
                    continue;
                }

                if (tx != null) await tx.CommitAsync(ct);

                if (!string.IsNullOrWhiteSpace(oldReportPath) && !string.Equals(oldReportPath, newReportPath, StringComparison.OrdinalIgnoreCase) && File.Exists(oldReportPath))
                {
                    try { File.Delete(oldReportPath); } catch { }
                }

                return true;
            }
            catch (Exception ex)
            {
                if (newReportPath != null && File.Exists(newReportPath))
                {
                    try { File.Delete(newReportPath); } catch { }
                }
                logger?.LogError(ex, "Transaction failed saving undone exclusion {ScopeCaseId} for job {JobId}", scopeCaseId, job.PreLoginReportJobId);
                if (tx != null) await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                throw;
            }
        }

        logger?.LogError("Exceeded max retries syncing undone exclusion {ScopeCaseId} due to concurrency conflicts.", scopeCaseId);
        return false;
    }

    private async Task<string?> TryPublishReportAsync(
        PreLoginReportJob job,
        InstaReportData updatedData,
        int nextVersion,
        CancellationToken ct)
    {
        if (_reports is null || _environment is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(job.ReportStoragePath) && job.Status != PreLoginReportJobStatus.Completed)
        {
            return null;
        }

        var format = (Enum.TryParse<PreLoginReportFormat>(job.Format, true, out var f) && !(updatedData.Company?.LitigationOnly == true && f != PreLoginReportFormat.Sbi)) ? f : PreLoginReportFormat.Sbi;
        var identifier = (updatedData.Company?.LitigationOnly == true)
            ? (updatedData.Company.RegistrationNumber ?? job.Cin ?? "UNKNOWN")
            : (job.Cin ?? "UNKNOWN");

        var generated = await _reports.GenerateFromDataAsync(identifier, format, updatedData, ct);

        var directory = Path.Combine(_environment.ContentRootPath, "App_Data", "PreLoginReports");
        Directory.CreateDirectory(directory);
        var attemptId = Guid.NewGuid().ToString("N");
        var versionedFileName = $"{job.PreLoginReportJobId}-v{nextVersion}-{attemptId}-{generated.FileName}";
        var newReportPath = Path.Combine(directory, versionedFileName);
        await File.WriteAllBytesAsync(newReportPath, generated.Bytes, ct);
        return newReportPath;
    }

    private static bool TrySyncCasesToDataJson(
        string? dataJson,
        IReadOnlyDictionary<string, string?> exclusionMap,
        bool isExcludedDefault,
        out string? updatedJson,
        out string? errorMessage)
        => TrySyncCasesToDataJson(dataJson, exclusionMap, isExcludedDefault, out updatedJson, out _, out errorMessage);

    private static bool TrySyncCasesToDataJson(
        string? dataJson,
        IReadOnlyDictionary<string, string?> exclusionMap,
        bool isExcludedDefault,
        out string? updatedJson,
        out InstaReportData? updatedData,
        out string? errorMessage)
    {
        if (exclusionMap.Count == 0)
        {
            updatedJson = dataJson;
            updatedData = null;
            errorMessage = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(dataJson))
        {
            updatedJson = null;
            updatedData = null;
            errorMessage = "Job DataJson is null or whitespace.";
            return false;
        }

        InstaReportData? data;
        try
        {
            data = JsonSerializer.Deserialize<InstaReportData>(dataJson);
        }
        catch (Exception ex)
        {
            updatedJson = null;
            updatedData = null;
            errorMessage = $"Failed to deserialize DataJson: {ex.Message}";
            return false;
        }

        if (data?.LegalCases?.Cases is null)
        {
            updatedJson = null;
            updatedData = null;
            errorMessage = "DataJson does not contain LegalCases.Cases.";
            return false;
        }

        var cases = data.LegalCases.Cases.ToList();
        var matchedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < cases.Count; i++)
        {
            var existing = cases[i];
            if (exclusionMap.TryGetValue(existing.CaseRowId, out var reason))
            {
                cases[i] = existing with
                {
                    IsExcludedFromReport = isExcludedDefault,
                    ExclusionReason = isExcludedDefault ? (reason ?? existing.ExclusionReason) : null
                };
                matchedAliases.Add(existing.CaseRowId);
            }
        }

        if (matchedAliases.Count != exclusionMap.Count)
        {
            var missing = exclusionMap.Keys.Except(matchedAliases, StringComparer.OrdinalIgnoreCase);
            updatedJson = null;
            updatedData = null;
            errorMessage = $"Missing cases in DataJson: {string.Join(", ", missing)}";
            return false;
        }

        var updatedLegalCases = LegalCaseFileParser.ToInstaLegalCases(cases) with { NextCaseRowIdIndex = data.LegalCases.NextCaseRowIdIndex };
        updatedData = data with { LegalCases = updatedLegalCases };
        updatedJson = JsonSerializer.Serialize(updatedData);
        errorMessage = null;
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

    public async Task<PreLoginTrustReport> GetTrustReportAsync(long? jobId = null, DateTime? fromUtc = null, DateTime? toUtc = null, CancellationToken ct = default)
    {
        var query = db.PreLoginAttributionDecisions.AsNoTracking();
        if (jobId.HasValue) query = query.Where(d => d.PreLoginReportJobId == jobId.Value);
        if (fromUtc.HasValue) query = query.Where(d => d.DecidedUtc >= fromUtc.Value);
        if (toUtc.HasValue) query = query.Where(d => d.DecidedUtc < toUtc.Value);

        var decisions = await query.ToListAsync(ct);

        var groupedByReason = decisions.GroupBy(d => d.ReasonKind).OrderBy(g => g.Key);
        var rows = new List<TrustReportRow>();

        int totalSuggested = 0, totalAccepted = 0, totalAutoApplied = 0, totalRejected = 0, totalUndone = 0, totalMisses = 0;

        foreach (var g in groupedByReason)
        {
            var suggested = g.Count(d => d.Action == PreLoginAttributionAction.Suggested);
            var accepted = g.Count(d => d.Action == PreLoginAttributionAction.Accepted);
            var rejected = g.Count(d => d.Action == PreLoginAttributionAction.Rejected);
            var autoApplied = g.Count(d => d.Action == PreLoginAttributionAction.AutomaticallyApplied);
            var undone = g.Count(d => d.Action == PreLoginAttributionAction.Undone);
            var miss = g.Count(d => d.Action == PreLoginAttributionAction.AnalystMiss);

            totalSuggested += suggested;
            totalAccepted += accepted;
            totalAutoApplied += autoApplied;
            totalRejected += rejected;
            totalUndone += undone;
            totalMisses += miss;

            var correct = accepted + (autoApplied - undone);
            var denominator = accepted + autoApplied + rejected;
            var precision = denominator > 0 ? (double?)Math.Round((double)Math.Max(0, correct) / denominator, 4) : null;

            rows.Add(new TrustReportRow(g.Key, suggested, accepted, rejected, autoApplied, undone, miss, precision));
        }

        var groupedByTarget = decisions.GroupBy(d => d.TargetKind).OrderBy(g => g.Key);
        var targetRows = new List<TrustReportTargetRow>();

        foreach (var g in groupedByTarget)
        {
            var suggested = g.Count(d => d.Action == PreLoginAttributionAction.Suggested);
            var accepted = g.Count(d => d.Action == PreLoginAttributionAction.Accepted);
            var rejected = g.Count(d => d.Action == PreLoginAttributionAction.Rejected);
            var autoApplied = g.Count(d => d.Action == PreLoginAttributionAction.AutomaticallyApplied);
            var undone = g.Count(d => d.Action == PreLoginAttributionAction.Undone);
            var miss = g.Count(d => d.Action == PreLoginAttributionAction.AnalystMiss);

            var correct = accepted + (autoApplied - undone);
            var denominator = accepted + autoApplied + rejected;
            var precision = denominator > 0 ? (double?)Math.Round((double)Math.Max(0, correct) / denominator, 4) : null;

            targetRows.Add(new TrustReportTargetRow(g.Key, suggested, accepted, rejected, autoApplied, undone, miss, precision));
        }

        var totalCorrect = totalAccepted + (totalAutoApplied - totalUndone);
        var totalDenominator = totalAccepted + totalAutoApplied + totalRejected;
        var overallPrecision = totalDenominator > 0 ? (double?)Math.Round((double)Math.Max(0, totalCorrect) / totalDenominator, 4) : null;

        return new PreLoginTrustReport(
            jobId,
            fromUtc,
            toUtc,
            rows,
            targetRows,
            decisions.Count,
            totalSuggested,
            totalAccepted,
            totalRejected,
            totalAutoApplied,
            totalUndone,
            totalMisses,
            overallPrecision);
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

