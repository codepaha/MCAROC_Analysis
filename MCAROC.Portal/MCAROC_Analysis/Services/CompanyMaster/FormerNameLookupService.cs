using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>Finds companies that were registered under a name before (#497).</summary>
public interface IFormerNameLookup
{
    Task<FormerNameLookup> FindAsync(string name, CancellationToken ct);
}

/// <summary>Former names from the reference tool's free company search (a hit whose context is <c>oldName</c>), kept in
/// <see cref="CompanyFormerName"/> so the same name later resolves without a search. A name already stored is answered from the store. A search
/// that fails, or a tool that isn't configured, reports <see cref="FormerNameLookup.NotSearched"/>, and resolution falls back to the master alone,
/// as before; it never guesses.</summary>
public sealed class FormerNameLookupService(AppDbContext db, ReferenceToolClient client, TimeProvider time, ILogger<FormerNameLookupService> logger) : IFormerNameLookup
{
    public const string SearchSource = "reference-tool-search";
    private const int SearchLimit = 25;

    public async Task<FormerNameLookup> FindAsync(string name, CancellationToken ct)
    {
        var normalized = CompanyNameNormalizer.Normalize(name).NameNormalized;
        if (normalized.Length == 0) return FormerNameLookup.NotSearched;

        var stored = await db.CompanyFormerNames.AsNoTracking().Where(f => f.FormerNameNormalized == normalized).ToListAsync(ct);
        if (stored.Count > 0)
            return new FormerNameLookup(true,
                stored.Select(f => new FormerNameHit(f.Identifier, f.FormerName, f.CurrentNameWhenSeen, null, null, null, null)).ToList(), []);

        if (!client.IsConfigured) return FormerNameLookup.NotSearched;
        IReadOnlyList<ReferenceCompanyHint> hits;
        try
        {
            hits = await client.SearchCompaniesAsync(name.Trim(), SearchLimit, ct);
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Former-name search failed for '{Name}'; resolving from the master alone", name);
            return FormerNameLookup.NotSearched;
        }

        var former = hits.Where(h => h.IsFormerNameMatch && !string.IsNullOrWhiteSpace(h.NameHint)).ToList();
        await StoreAsync(former, ct);
        var currentHolders = hits
            .Where(h => string.Equals(h.Context, "legalName", StringComparison.OrdinalIgnoreCase)
                && CompanyNameNormalizer.Normalize(h.LegalName).NameNormalized == normalized)
            .Select(h => h.Cin).ToList();
        return new FormerNameLookup(true,
            former.Select(h => new FormerNameHit(h.Cin, h.NameHint!, h.LegalName, h.Status, h.State, h.PinCode, h.IncorporationDate)).ToList(),
            currentHolders);
    }

    /// <summary>Every former name the search reported, not only the requested one: a later request by any of them then resolves from the store.</summary>
    private async Task StoreAsync(IReadOnlyList<ReferenceCompanyHint> former, CancellationToken ct)
    {
        if (former.Count == 0) return;
        var now = time.GetUtcNow().UtcDateTime;
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
            catch (DbUpdateException) { db.ChangeTracker.Clear(); } // stored by a concurrent search: theirs is as good
        }
    }
}
