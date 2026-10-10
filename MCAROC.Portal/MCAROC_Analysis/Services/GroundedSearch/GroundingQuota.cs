using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.GroundedSearch;

public sealed class GroundedSearchOptions
{
    public const string SectionName = "GroundedSearch";

    /// <summary>Model that runs the search. Gemma cannot ground, so this has to be a Gemini model.</summary>
    public string Model { get; set; } = "gemini-3.1-flash-lite";

    /// <summary>The working cap on Google Search queries per calendar month (UTC): a search is started only if the month's count plus
    /// <see cref="ReservedQueriesPerCall"/> stays at or below this. 5,000 queries a month are free across the Gemini 3 models, and each query past
    /// that is billed at $14 per 1,000, so this sits 50 under the free allowance.</summary>
    public int SoftCapQueries { get; set; } = 4950;

    /// <summary>The hard stop: no search starts if the month's count (searches in flight included) plus its reserve would pass this, whatever the soft cap says. The
    /// 100 queries between the two absorb a call that runs more queries than it reserved. Nothing can stop a call mid-way - the API has no setting
    /// that bounds how many queries one call runs, and a model ran 12 when told to run 5 - so the buffer is a budget policy, not a mathematical
    /// bound. The most a month can then cost beyond the free allowance is 50 queries ($0.70) if the buffer is fully used.</summary>
    public int HardStopQueries { get; set; } = 5050;

    /// <summary>Queries held back for each search while it runs, and the number the search is told it may run at most (a hint, not a limit). The
    /// model chooses how many queries one call makes (3 on the default model; up to 14 seen on a larger one), so this covers the largest observed.</summary>
    public int ReservedQueriesPerCall { get; set; } = 15;

    /// <summary>The search model must be a Flash-Lite model: those ran 3 queries per search in every test, while gemini-3.5-flash ran 11-13 and
    /// ignored an instruction to run 5. Set true only after measuring a larger model's query counts.</summary>
    public bool AllowLargerSearchModel { get; set; }

    /// <summary>True when <see cref="Model"/> is one whose query count was measured to stay small, or the larger model was explicitly allowed.</summary>
    public bool ModelHasBoundedQueries => AllowLargerSearchModel || Model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase);

    public int TimeoutSeconds { get; set; } = 90;
}

/// <summary>Counts the month's grounding queries and refuses a search that could take the month past <see cref="GroundedSearchOptions.SoftCapQueries"/>, and every search once the count reaches <see cref="GroundedSearchOptions.HardStopQueries"/>.
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

    public int Limit => _options.Value.SoftCapQueries;

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
            var counted = _used + _reserved;
            if (counted + want > _options.Value.HardStopQueries) return null;              // hard stop: never start a search whose reserve would cross it
            if (counted + want > _options.Value.SoftCapQueries) return null;               // working cap
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
