using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>Finds companies that were registered under a name before, and those that have it today (#497).</summary>
public interface IFormerNameLookup
{
    Task<FormerNameLookup> FindAsync(string name, CancellationToken ct);

    /// <summary>Whether a search can be made at all. A lookup that is not configured is a decided policy (the master alone); a configured one that
    /// reports <see cref="FormerNameLookup.NotSearched"/> failed, and its answer is unknown.</summary>
    bool IsConfigured => true;
}

/// <summary>Former names from the reference tool's free company search (a hit whose context is <c>oldName</c>), and current holders (<c>legalName</c>),
/// read page by page so the result says whether the search was read to its end. Each search is kept whole in <see cref="CompanyNameSearch"/>: a later
/// request by the same name gets exactly what this search showed (conflicts and completeness included) until it is <see cref="MaxAge"/> old.
/// Every former name seen is also kept in <see cref="CompanyFormerName"/> (the request page reads it). A search that fails, or a tool that isn't
/// configured, reports <see cref="FormerNameLookup.NotSearched"/>, and resolution falls back to the master alone, as before; it never guesses.</summary>
public sealed class FormerNameLookupService(AppDbContext db, ReferenceToolClient client, TimeProvider time, ILogger<FormerNameLookupService> logger) : IFormerNameLookup
{
    public const string SearchSource = "reference-tool-search";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public const int PageSize = 25;
    public const int MaxPages = 4;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Observation(IReadOnlyList<FormerNameHit> FormerNames, IReadOnlyList<CompanyHolder> CurrentHolders);

    public bool IsConfigured => client.IsConfigured;

    public async Task<FormerNameLookup> FindAsync(string name, CancellationToken ct)
    {
        var normalized = CompanyNameNormalizer.Normalize(name).NameNormalized;
        if (normalized.Length == 0 || normalized.Length > 450) return FormerNameLookup.NotSearched;
        var now = time.GetUtcNow().UtcDateTime;

        var stored = await db.CompanyNameSearches.AsNoTracking().FirstOrDefaultAsync(s => s.NameNormalized == normalized, ct);
        if (stored is not null && now - stored.SearchedUtc < MaxAge
            && JsonSerializer.Deserialize<Observation>(stored.ResultJson, Json) is { } kept)
            return new FormerNameLookup(true, stored.Complete, kept.FormerNames ?? [], kept.CurrentHolders ?? []);

        if (!client.IsConfigured) return FormerNameLookup.NotSearched;
        ReferenceSearchResult result;
        try
        {
            result = await client.SearchCompaniesExhaustivelyAsync(name.Trim(), PageSize, MaxPages, ct);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Former-name search failed for '{Name}'; resolving from the master alone", name);
            return FormerNameLookup.NotSearched;
        }

        var former = result.Hits.Where(h => h.IsFormerNameMatch && !string.IsNullOrWhiteSpace(h.NameHint)).ToList();
        var observation = new Observation(
            former.Select(h => new FormerNameHit(h.Cin, h.NameHint!, h.LegalName, h.Status, h.State, h.PinCode, h.IncorporationDate)).ToList(),
            result.Hits.Where(h => string.Equals(h.Context, "legalName", StringComparison.OrdinalIgnoreCase)
                    && CompanyNameNormalizer.Normalize(h.LegalName).NameNormalized == normalized)
                .Select(h => new CompanyHolder(h.Cin, h.LegalName, h.Status, h.State, h.PinCode, h.IncorporationDate)).ToList());

        await KeepSearchAsync(normalized, result.Complete, observation, now, ct);
        await KeepFormerNamesAsync(former, now, ct);
        return new FormerNameLookup(true, result.Complete, observation.FormerNames, observation.CurrentHolders);
    }

    private async Task KeepSearchAsync(string normalized, bool complete, Observation observation, DateTime now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(observation, Json);
        var updated = await db.CompanyNameSearches.Where(s => s.NameNormalized == normalized)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.SearchedUtc, now).SetProperty(x => x.Complete, complete).SetProperty(x => x.ResultJson, json), ct);
        if (updated > 0) return;
        db.CompanyNameSearches.Add(new CompanyNameSearch { NameNormalized = normalized, SearchedUtc = now, Complete = complete, ResultJson = json });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ChangeTracker.Clear(); } // kept by a concurrent search for the same name
    }

    /// <summary>Every former name the search reported, not only the requested one: the request page shows "its former name" from these.</summary>
    private async Task KeepFormerNamesAsync(IReadOnlyList<ReferenceCompanyHint> former, DateTime now, CancellationToken ct)
    {
        foreach (var h in former.DistinctBy(h => (CompanyNameNormalizer.Normalize(h.NameHint).NameNormalized, h.Cin)))
        {
            var key = CompanyNameNormalizer.Normalize(h.NameHint).NameNormalized;
            if (key.Length == 0 || key.Length > 450) continue;
            var updated = await db.CompanyFormerNames.Where(f => f.FormerNameNormalized == key && f.Identifier == h.Cin)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.LastSeenUtc, now).SetProperty(f => f.CurrentNameWhenSeen, h.LegalName), ct);
            if (updated > 0) continue;
            db.CompanyFormerNames.Add(new CompanyFormerName
            {
                Identifier = h.Cin, FormerName = h.NameHint!.Trim(), FormerNameNormalized = key, CurrentNameWhenSeen = h.LegalName,
                Source = SearchSource, FirstSeenUtc = now, LastSeenUtc = now
            });
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); }
        }
    }
}
