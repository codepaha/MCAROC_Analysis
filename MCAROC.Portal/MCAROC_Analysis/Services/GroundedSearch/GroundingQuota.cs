using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.GroundedSearch;

public sealed class GroundedSearchOptions
{
    public const string SectionName = "GroundedSearch";

    /// <summary>Model that runs the search. Gemma cannot ground, so this has to be a Gemini model.</summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>Google Search queries allowed per calendar month (UTC). 5,000 is the free allowance across the Gemini 3 models; each query past it
    /// is billed at $14 per 1,000. A search is only started when <see cref="ReservedQueriesPerCall"/> queries are still free, and once the month's
    /// total reaches the limit (or leaves less than that reserve) searches are refused until the month rolls over.
    /// <para>The guarantee is therefore: <b>no search starts unless the month stays within the limit if that search runs no more than
    /// <see cref="ReservedQueriesPerCall"/> queries.</b> The API has no setting that bounds how many queries one call runs. The call is also told
    /// in its instructions to stay within the reserve, but that is a hint, not a limit: measured on 2026-10-10, the default gemini-3.1-flash-lite ran
    /// 3 queries however it was asked, while gemini-3.5-flash ran 11-13 even when told to run at most 5. So keep the search model on a Flash-Lite
    /// model. If a call does run past its reserve the extra queries are counted (and logged), and the month can end past the limit by at most that
    /// one call's excess - $0.014 per query.</para></summary>
    public int MonthlyQueryLimit { get; set; } = 5000;

    /// <summary>Queries held back for each search while it runs, and the number the search is told it may run at most. The model chooses how many
    /// queries one call makes (2-3 is typical; 14 was seen on the larger model), so this covers the largest call observed.</summary>
    public int ReservedQueriesPerCall { get; set; } = 15;

    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>Counts the month's grounding queries and refuses a search that could take the month past <see cref="GroundedSearchOptions.MonthlyQueryLimit"/>.
/// The month's total is read once from the saved usage rows (so a restart keeps it) and then kept in memory; searches in flight hold a reservation.</summary>
public sealed class GroundingQuota
{
    private readonly Func<DateTime, CancellationToken, Task<int>> _loadUsedSince;
    private readonly IOptions<GroundedSearchOptions> _options;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private (int Year, int Month)? _month;
    private int _used;
    private int _reserved;

    public GroundingQuota(IServiceScopeFactory scopes, IOptions<GroundedSearchOptions> options)
        : this(async (monthStart, ct) =>
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.AiUsageRecords.Where(r => r.GroundingQueries > 0 && r.CreatedUtc >= monthStart).SumAsync(r => r.GroundingQueries, ct);
        }, options, () => DateTime.UtcNow) { }

    public GroundingQuota(Func<DateTime, CancellationToken, Task<int>> loadUsedSince, IOptions<GroundedSearchOptions> options, Func<DateTime> utcNow)
    {
        _loadUsedSince = loadUsedSince;
        _options = options;
        _utcNow = utcNow;
    }

    public int Limit => _options.Value.MonthlyQueryLimit;

    /// <summary>Queries used so far this month (including searches still running).</summary>
    public async Task<int> UsedThisMonthAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await EnsureMonthAsync(ct); return _used + _reserved; }
        finally { _gate.Release(); }
    }

    /// <summary>Holds queries for one search. Null when the month's allowance cannot cover it. Pass the result to <see cref="Complete"/>.</summary>
    public async Task<int?> TryReserveAsync(CancellationToken ct)
    {
        var want = Math.Max(1, _options.Value.ReservedQueriesPerCall);
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureMonthAsync(ct);
            if (_used + _reserved + want > _options.Value.MonthlyQueryLimit) return null;
            _reserved += want;
            return want;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Releases a reservation and counts the queries the search actually ran (0 when it failed before searching).</summary>
    public void Complete(int reserved, int actualQueries)
    {
        _gate.Wait();
        try
        {
            _reserved = Math.Max(0, _reserved - reserved);
            _used += Math.Max(0, actualQueries);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureMonthAsync(CancellationToken ct)
    {
        var now = _utcNow();
        var key = (now.Year, now.Month);
        if (_month == key) return;
        _used = await _loadUsedSince(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc), ct);
        _month = key;
    }
}
