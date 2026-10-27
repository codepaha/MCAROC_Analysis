using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData.Attribution;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary><see cref="TooMany"/>: the search already carries the most names it can, so the variation would never be searched.</summary>
public enum NameVariationAddStatus { Added, AlreadySearched, TooGeneric, Invalid, TooMany }

/// <param name="Warning">Set for an added variation that may bring other companies' cases (a single distinctive word).</param>
public sealed record NameVariationAddResult(string Name, NameVariationAddStatus Status, string Message, string? Warning = null);

/// <param name="Search">What starting the search with the new names did; null when nothing was added.</param>
public sealed record NameVariationAddOutcome(IReadOnlyList<NameVariationAddResult> Results, LitigationStartResult? Search, string? Problem = null)
{
    public int AddedCount => Results.Count(r => r.Status == NameVariationAddStatus.Added);
}

/// <summary>Name variations a person adds for a company on the Litigation tab (owner, 2026-10-26): later found to be how its cases are filed, they are
/// searched from then on (<see cref="CompanyNameVariation"/>, kept per CIN/LLPIN). Adding one re-runs the request's search with the full keyword set (one
/// paid search; the cases it returns are matched to the stored ones as every refresh is and attributed as usual, a party that is exactly the variation being the
/// company's), and sends back to attribution the cases already decided otherwise whose party is exactly the new variation. Removing one stops it being
/// searched and sends back the cases it alone decided. A search already running for the request picks the new names up afterwards
/// (<see cref="NameVariationSearchWorker"/>).</summary>
public sealed class NameVariationService(
    AppDbContext db, LitigationStartService start, ILogger<NameVariationService> logger,
    IOptions<BprLitigationOptions>? bprOptions = null, LitigationKeywordSuggester? suggester = null)
{
    public const int MaxLength = 200;
    public const int MaxPerAdd = 10;

    public async Task<NameVariationAddOutcome> AddAsync(long requestId, IReadOnlyList<string> names, string actor, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        var identifier = (request?.Cin ?? request?.Llpin)?.Trim().ToUpperInvariant();
        if (request is null || string.IsNullOrEmpty(identifier))
            return new([], null, "Name variations are kept per company, so the request needs a CIN or LLPIN first.");

        var taken = await TakenNamesAsync(request, identifier, ct);
        var results = new List<NameVariationAddResult>();
        var added = new List<CompanyNameVariation>();
        var now = DateTime.UtcNow;
        foreach (var raw in names.Select(n => string.Join(' ', (n ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                     .Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxPerAdd))
        {
            var key = WholeNamePartyMatcher.Canonical(raw);
            if (raw.Length > MaxLength || key.Length == 0)
            {
                results.Add(new(raw, NameVariationAddStatus.Invalid, raw.Length > MaxLength ? $"Longer than {MaxLength} characters." : "Not a name."));
                continue;
            }
            if (taken.TryGetValue(key, out var already))
            {
                results.Add(new(raw, NameVariationAddStatus.AlreadySearched, $"Already searched as {already}."));
                continue;
            }
            var distinctive = CaseAttributionRules.DistinctiveWords(raw).Distinct().Count();
            if (distinctive == 0)
            {
                results.Add(new(raw, NameVariationAddStatus.TooGeneric, "Only generic words (Limited, India, Company...): it would find every company's cases."));
                continue;
            }
            var variation = new CompanyNameVariation
            {
                Identifier = identifier, Name = raw, NameKey = key, AddedBy = actor, AddedUtc = now, AddedOnRequestId = requestId
            };
            added.Add(variation);
            taken[key] = $"the name variation \"{raw}\"";
            results.Add(new(raw, NameVariationAddStatus.Added, "Added.", distinctive == 1
                ? $"\"{raw}\" has one distinctive word, so the search may also return other companies' cases (\"... {raw}\"). They are set aside unless a party is exactly this name or is judged to be the company."
                : null));
        }
        if (added.Count == 0) return new(results, null);

        db.CompanyNameVariations.AddRange(added);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("IX_CompanyNameVariations", StringComparison.Ordinal) == true)
        {
            // Added at the same moment by someone else: theirs stands.
            foreach (var v in added) db.Entry(v).State = EntityState.Detached;
            return new([.. results.Select(r => r.Status == NameVariationAddStatus.Added ? r with { Status = NameVariationAddStatus.AlreadySearched, Message = "Just added by someone else.", Warning = null } : r)], null);
        }
        // A variation the search cannot carry (it holds at most LitigationKeywordSuggester.MaxKeywords names) would never be searched, and a
        // later pass would keep seeing it as unsearched: it is not kept.
        var misfits = await NotInPlanAsync(request, added, ct);
        if (misfits.Count > 0)
        {
            var ids = misfits.Select(v => v.CompanyNameVariationId).ToList();
            await db.CompanyNameVariations.Where(v => ids.Contains(v.CompanyNameVariationId)).ExecuteDeleteAsync(ct);
            added = added.Where(v => !ids.Contains(v.CompanyNameVariationId)).ToList();
            var misfitNames = misfits.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
            results = results.Select(r => r.Status == NameVariationAddStatus.Added && misfitNames.Contains(r.Name)
                ? r with { Status = NameVariationAddStatus.TooMany, Warning = null,
                    Message = $"The search already carries the most names it can ({LitigationKeywordSuggester.MaxKeywords}); remove a name variation first." }
                : r).ToList();
            if (added.Count == 0) return new(results, null);
        }

        logger.LogInformation("{Actor} added name variation(s) {Names} for {Identifier} on request {RequestId}",
            actor, string.Join(", ", added.Select(v => v.Name)), identifier, requestId);

        // Cases already decided otherwise whose party is exactly a new variation are the company's now: they go back to attribution.
        await ReassessAsync(identifier, c => c.AttributionOutcome != LitigationAttributionOutcome.Attributed
            && LitigationAttributionService.PartiesOf(c).Any(p => CaseAttributionRules.MatchingVariation(p, added.Select(v => v.Name).ToList()) is not null),
            $"Assessed again: the name variation(s) {string.Join(", ", added.Select(v => $"\"{v.Name}\""))} were added.", ct);

        return new(results, await StartSearchAsync(requestId, ct));
    }

    public async Task<bool> RemoveAsync(long requestId, long variationId, string actor, CancellationToken ct)
    {
        var identifier = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.Cin ?? r.Llpin).FirstOrDefaultAsync(ct);
        identifier = identifier?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(identifier)) return false;
        var now = DateTime.UtcNow;
        var removed = await db.CompanyNameVariations
            .Where(v => v.CompanyNameVariationId == variationId && v.Identifier == identifier && v.RemovedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.RemovedUtc, now).SetProperty(v => v.RemovedBy, actor), ct);
        if (removed == 0) return false;
        var variation = await db.CompanyNameVariations.AsNoTracking().SingleAsync(v => v.CompanyNameVariationId == variationId, ct);
        logger.LogInformation("{Actor} removed name variation {Name} for {Identifier}", actor, variation.Name, identifier);

        // The cases this variation alone made the company's are assessed again without it.
        await ReassessAsync(identifier, c => c.AttributionOutcome == LitigationAttributionOutcome.Attributed
            && c.AttributionRule == CaseAttributionRules.NameVariationRule
            && c.AttributionParty != null && WholeNamePartyMatcher.Canonical(c.AttributionParty) == variation.NameKey,
            $"Assessed again: the name variation \"{variation.Name}\" was removed.", ct);
        return true;
    }

    /// <summary>Re-runs the request's search with the full keyword set (registered, former and added names, and the generated forms): the case list
    /// shows one report, so a search for the new names alone would hide every other case. One paid search, recorded as a manual one.</summary>
    /// <summary>The variations the request's search plan would leave out (none when the plan cannot be made: the search then fails anyway).</summary>
    private async Task<List<CompanyNameVariation>> NotInPlanAsync(McaRequest request, IReadOnlyList<CompanyNameVariation> variations, CancellationToken ct)
    {
        var (plan, _) = await LitigationStartService.PlanSearchAsync(db, request, bprOptions?.Value ?? new BprLitigationOptions(), ct, suggester);
        if (plan is null) return [];
        var planned = plan.Keywords.Select(k => WholeNamePartyMatcher.Canonical(k.Value)).ToHashSet(StringComparer.Ordinal);
        return variations.Where(v => !planned.Contains(v.NameKey)).ToList();
    }

    /// <summary>The search a pass of <see cref="NameVariationSearchWorker"/> runs for variations added while another search was running. It
    /// starts only when the plan carries a variation the latest search did not and is not that search's keyword set again, so each variation
    /// buys at most one search this way (the job stores its keywords when it is created, a failed search included) and an unchanged keyword
    /// set is never bought twice. Null when there is nothing to search.</summary>
    public async Task<LitigationStartResult?> StartPendingSearchAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return null;
        var unsearched = await UnsearchedAsync(db, request, ct);
        if (unsearched.Count == 0) return null;
        var (plan, _) = await LitigationStartService.PlanSearchAsync(db, request, bprOptions?.Value ?? new BprLitigationOptions(), ct, suggester);
        if (plan is null) return null;
        var planned = plan.Keywords.Select(k => WholeNamePartyMatcher.Canonical(k.Value)).ToHashSet(StringComparer.Ordinal);
        if (!unsearched.Any(v => planned.Contains(v.NameKey))) return null;
        var lastJson = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId)
            .OrderByDescending(j => j.LitigationSearchJobId).Select(j => j.KeywordsJson).FirstOrDefaultAsync(ct);
        var last = LitigationKeyword.ParseJson(lastJson).Select(k => WholeNamePartyMatcher.Canonical(k.Value)).ToHashSet(StringComparer.Ordinal);
        if (planned.SetEquals(last)) return null;
        return await start.StartSearchAsync(request, plan.Keywords, plan.EntityType, plan.ApplicationCustomerId, PaidCallTrigger.Manual, ct);
    }

    public async Task<LitigationStartResult> StartSearchAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return LitigationStartResult.Ineligible("The request no longer exists.");
        var (plan, problem) = await LitigationStartService.PlanSearchAsync(db, request, bprOptions?.Value ?? new BprLitigationOptions(), ct, suggester);
        if (plan is null) return LitigationStartResult.Ineligible(problem!);
        return await start.StartSearchAsync(request, plan.Keywords, plan.EntityType, plan.ApplicationCustomerId, PaidCallTrigger.Manual, ct);
    }

    /// <summary>The company's active variations the request's latest search did not cover: what a search still has to run for.</summary>
    public static async Task<IReadOnlyList<CompanyNameVariation>> UnsearchedAsync(AppDbContext db, McaRequest request, CancellationToken ct)
    {
        var variations = await LitigationCompanyNames.ActiveVariationsAsync(db, request.Cin ?? request.Llpin, ct);
        if (variations.Count == 0) return [];
        var keywordsJson = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == request.RequestId)
            .Select(j => j.KeywordsJson).FirstOrDefaultAsync(ct);
        var searched = LitigationKeyword.ParseJson(keywordsJson).Select(k => WholeNamePartyMatcher.Canonical(k.Value)).ToHashSet(StringComparer.Ordinal);
        return variations.Where(v => !searched.Contains(v.NameKey)).ToList();
    }

    /// <summary>Every name the company is already searched under, by canonical key, with how it is searched (for "already searched as ...").</summary>
    private async Task<Dictionary<string, string>> TakenNamesAsync(McaRequest request, string identifier, CancellationToken ct)
    {
        var taken = new Dictionary<string, string>(StringComparer.Ordinal);
        void Take(string? name, string how)
        {
            var key = WholeNamePartyMatcher.Canonical(name);
            if (key.Length > 0) taken.TryAdd(key, how);
        }
        Take(request.CompanyName, "the registered name");
        if (request.LatestCompletedIngestionRunId is { } runId)
            foreach (var former in await db.CompanyNameHistories.AsNoTracking().Where(h => h.IngestionRunId == runId && h.PreviousName != null)
                         .Select(h => h.PreviousName!).ToListAsync(ct))
                Take(former, $"the former name \"{former}\"");
        foreach (var v in await LitigationCompanyNames.ActiveVariationsAsync(db, identifier, ct))
            Take(v.Name, $"the name variation \"{v.Name}\"");
        return taken;
    }

    /// <summary>Sends back to attribution the cases of every request for the company that <paramref name="affected"/> picks, and moves those requests'
    /// live-data version so every litigation view and the analysis follow.</summary>
    private async Task ReassessAsync(string identifier, Func<LitigationCase, bool> affected, string reason, CancellationToken ct)
    {
        var requestIds = await db.Requests.AsNoTracking().Where(r => r.Cin == identifier || r.Llpin == identifier).Select(r => r.RequestId).ToListAsync(ct);
        foreach (var requestId in requestIds)
        {
            var cases = await db.LitigationCases.Where(c => c.RequestId == requestId && c.MergedIntoCaseId == null && c.AttributionOutcome != null).ToListAsync(ct);
            var picked = cases.Where(affected).ToList();
            if (picked.Count == 0) continue;
            foreach (var c in picked)
            {
                c.AttributionOutcome = LitigationAttributionOutcome.Pending;
                c.AttributionRule = null;
                c.AttributionParty = null;
                c.AttributionReason = reason;
            }
            await db.SaveChangesAsync(ct);
            await RequestLiveData.BumpAsync(db, requestId, ct);
            logger.LogInformation("{Count} case(s) of request {RequestId} sent back to attribution: {Reason}", picked.Count, requestId, reason);
        }
    }
}
