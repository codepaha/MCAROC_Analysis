using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Whether the lexical half of hybrid retrieval (docs/chat-hybrid-search.md) can run on this server: SQL Server
/// Full-Text Search installed, and both chunk tables indexed. When it can't, retrieval silently falls back to vector
/// search per query (<see cref="HybridSearch.IsFullTextIndexedAsync"/>) — this exists so that fallback is visible.</summary>
public sealed record FullTextSearchState(bool Installed, bool DocumentChunksIndexed, bool LitigationChunksIndexed)
{
    public bool Available => Installed && DocumentChunksIndexed && LitigationChunksIndexed;

    /// <summary>Each table falls back on its own (<see cref="HybridSearch.IsFullTextIndexedAsync"/> is per table), so
    /// with one index missing, keyword search still runs over the other.</summary>
    public bool FilingsKeywordSearch => Installed && DocumentChunksIndexed;
    public bool OrdersKeywordSearch => Installed && LitigationChunksIndexed;

    /// <summary>What the chat panel tells the user, or null when keyword search covers everything.</summary>
    public string? ChatNote => (FilingsKeywordSearch, OrdersKeywordSearch) switch
    {
        (true, true) => null,
        (true, false) => "Keyword search covers MCA filings only on this server — answers about court orders use semantic search, so exact case numbers or section references in orders may be missed.",
        (false, true) => "Keyword search covers court orders only on this server — answers about MCA filings use semantic search, so exact SRNs or section references in filings may be missed.",
        (false, false) => "Keyword search is unavailable on this server — answers use semantic search only, so exact case numbers or section references may be missed."
    };
}

/// <summary>#349: reports <see cref="FullTextSearchState"/> to the startup log, <c>/health</c> and the chat panel. Cached
/// briefly — it changes only when someone installs the feature or runs the index migration, not per request.</summary>
public class FullTextSearchStatus(AppDbContext db, IMemoryCache cache)
{
    private const string CacheKey = "FullTextSearchState";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public virtual async Task<FullTextSearchState> GetAsync(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out FullTextSearchState? cached) && cached is not null)
            return cached;

        var row = await db.Database.SqlQueryRaw<StateRow>("""
            SELECT CAST(CASE WHEN FULLTEXTSERVICEPROPERTY('IsFullTextInstalled') = 1 THEN 1 ELSE 0 END AS bit) AS Installed,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.DocumentChunks')) THEN 1 ELSE 0 END AS bit) AS DocumentChunksIndexed,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID('dbo.LitigationOrderChunks')) THEN 1 ELSE 0 END AS bit) AS LitigationChunksIndexed
            """).SingleAsync(ct);
        var state = new FullTextSearchState(row.Installed, row.DocumentChunksIndexed, row.LitigationChunksIndexed);
        cache.Set(CacheKey, state, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = CacheFor });
        return state;
    }

    private sealed class StateRow
    {
        public bool Installed { get; init; }
        public bool DocumentChunksIndexed { get; init; }
        public bool LitigationChunksIndexed { get; init; }
    }
}

/// <summary>Logs once at startup when chat retrieval will run without its keyword half, with what to do about it.</summary>
public sealed class FullTextSearchStartupCheck(IServiceScopeFactory scopes, ILogger<FullTextSearchStartupCheck> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var state = await scope.ServiceProvider.GetRequiredService<FullTextSearchStatus>().GetAsync(stoppingToken);
            if (state.Available) return;
            var coverage = (state.FilingsKeywordSearch, state.OrdersKeywordSearch) switch
            {
                (false, false) => "OFF for MCA filings and court orders",
                (true, false) => "OFF for court orders (LitigationOrderChunks) — MCA filings still have it",
                _ => "OFF for MCA filings (DocumentChunks) — court orders still have it"
            };
            logger.LogWarning(
                "Chat keyword search is {Coverage}: SQL Server Full-Text Search installed={Installed}, DocumentChunks indexed={DocumentChunks}, " +
                "LitigationOrderChunks indexed={LitigationChunks}. Where it is off, answers use semantic search only, so exact case numbers and " +
                "section references can be missed. See docs/chat-hybrid-search.md.",
                coverage, state.Installed, state.DocumentChunksIndexed, state.LitigationChunksIndexed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not check SQL Server Full-Text Search status at startup.");
        }
    }
}
