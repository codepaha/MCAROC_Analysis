using MCAROC_Analysis.Services.GroundedSearch;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class GroundingQuotaTests
{
    private static GroundingQuota Quota(int alreadyUsed, Func<DateTime> now, int limit = 5000, int reserve = 10, List<DateTime>? loads = null) =>
        new((monthStart, _) => { loads?.Add(monthStart); return Task.FromResult(alreadyUsed); },
            Options.Create(new GroundedSearchOptions { MonthlyQueryLimit = limit, ReservedQueriesPerCall = reserve }), now);

    private static readonly DateTime June = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_search_is_allowed_while_the_whole_reservation_still_fits()
    {
        var quota = Quota(alreadyUsed: 4990, () => June);
        Assert.NotNull(await quota.TryReserveAsync(default));
    }

    [Fact]
    public async Task A_search_is_refused_when_the_reservation_would_pass_the_monthly_limit()
    {
        var quota = Quota(alreadyUsed: 4991, () => June);
        Assert.Null(await quota.TryReserveAsync(default));
    }

    [Fact]
    public async Task Searches_in_flight_hold_their_reservation()
    {
        var quota = Quota(alreadyUsed: 4985, () => June);
        var first = await quota.TryReserveAsync(default);
        Assert.NotNull(first);
        Assert.Null(await quota.TryReserveAsync(default));   // 4985 + 10 + 10 > 5000
        quota.Complete(first.Value, actualQueries: 2);        // only 2 were really run
        Assert.NotNull(await quota.TryReserveAsync(default)); // 4987 + 10 <= 5000
    }

    [Fact]
    public async Task A_failed_search_that_ran_no_query_costs_nothing()
    {
        var quota = Quota(alreadyUsed: 100, () => June);
        var held = await quota.TryReserveAsync(default);
        quota.Complete(held!.Value, actualQueries: 0);
        Assert.Equal(100, await quota.UsedThisMonthAsync(default));
    }

    [Fact]
    public async Task The_count_starts_over_in_a_new_month()
    {
        var now = June;
        var loads = new List<DateTime>();
        var quota = Quota(alreadyUsed: 5000, () => now, loads: loads);
        Assert.Null(await quota.TryReserveAsync(default));
        now = new DateTime(2026, 7, 1, 0, 0, 1, DateTimeKind.Utc);
        var july = Quota(alreadyUsed: 0, () => now);
        Assert.NotNull(await july.TryReserveAsync(default));
        Assert.Equal(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), loads.Single());
    }

    [Fact]
    public async Task One_quota_reloads_the_total_when_the_month_changes()
    {
        var now = June;
        var loads = new List<DateTime>();
        var used = 5000;
        var quota = new GroundingQuota((start, _) => { loads.Add(start); return Task.FromResult(used); },
            Options.Create(new GroundedSearchOptions()), () => now);
        Assert.Null(await quota.TryReserveAsync(default));
        used = 0;
        now = new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(await quota.TryReserveAsync(default));
        Assert.Equal(2, loads.Count);
    }
}
