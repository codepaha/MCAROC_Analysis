using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Where a request stands against its refresh allowance. <see cref="HasSearched"/> is false until the
/// first search has been paid for; that first search is never a refresh.</summary>
public sealed record LitigationRefreshState(
    bool HasSearched, int Used, int Max, DateTime? LastSearchUtc, DateTime? NextAllowedUtc, bool Allowed, string? Reason)
{
    public int Remaining => Math.Max(0, Max - Used);
}

/// <summary>Clients get the initial search plus a small number of refreshes, spaced apart. Counted from the
/// paid-call ledger (Committed searches), so a reused report — which costs nothing — is never counted, and a
/// released (provably unspent) reservation is given back.</summary>
public static class LitigationRefreshPolicy
{
    public static LitigationRefreshState Evaluate(int committedSearches, DateTime? lastSearchUtc, DateTime nowUtc, int maxRefreshes, int intervalDays)
    {
        if (committedSearches <= 0)
            return new LitigationRefreshState(false, 0, maxRefreshes, null, null, true, null);

        var used = committedSearches - 1;
        if (used >= maxRefreshes)
            return new LitigationRefreshState(true, used, maxRefreshes, lastSearchUtc, null, false,
                $"All {maxRefreshes} refreshes for this request have been used.");

        var next = lastSearchUtc?.AddDays(intervalDays);
        if (next is { } n && n > nowUtc)
            return new LitigationRefreshState(true, used, maxRefreshes, lastSearchUtc, n, false,
                $"Litigation data can next be refreshed on {Ist.Date(n)}.");

        return new LitigationRefreshState(true, used, maxRefreshes, lastSearchUtc, next, true, null);
    }

    public static async Task<LitigationRefreshState> GetAsync(AppDbContext db, long requestId, BprLitigationOptions opts, DateTime nowUtc, CancellationToken ct)
    {
        var searches = await db.PaidCallAdmissions.AsNoTracking()
            .Where(a => a.RequestId == requestId && a.Kind == PaidCallKind.LitigationSearch && a.State == PaidCallAdmissionState.Committed)
            .Select(a => a.ReservedUtc).ToListAsync(ct);
        return Evaluate(searches.Count, searches.Count == 0 ? null : searches.Max(), nowUtc, opts.MaxRefreshes, opts.RefreshIntervalDays);
    }
}
