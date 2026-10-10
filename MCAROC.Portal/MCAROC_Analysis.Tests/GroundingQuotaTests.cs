using MCAROC_Analysis.Services.GroundedSearch;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class GroundingQuotaTests
{
    private static GroundingQuota Quota(int alreadyUsed, Func<DateTime> now, int limit = 5000, int reserve = 10, List<DateTime>? loads = null) =>
        new((monthStart, _) => { loads?.Add(monthStart); return Task.FromResult(alreadyUsed); },
            Options.Create(new GroundedSearchOptions { MonthlyQueryLimit = limit, ReservedQueriesPerCall = reserve, SafetyMarginQueries = 0 }), now);

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

    [Fact]
    public async Task With_the_default_reserve_a_call_that_could_run_14_queries_is_not_started_at_4990_used()
    {
        // the largest call seen ran 14 queries: starting it with 4,990 used would end the month at 5,004
        var quota = new GroundingQuota((_, _) => Task.FromResult(4990), Options.Create(new GroundedSearchOptions()), () => June);
        Assert.True(new GroundedSearchOptions().ReservedQueriesPerCall >= 14);
        Assert.Null(await quota.TryReserveAsync(default));
    }

    [Fact]
    public async Task A_call_that_runs_past_its_reserve_is_counted_in_full_and_closes_the_month()
    {
        // the model ignores the limit it was given: 14 queries against a reserve of 10
        var quota = Quota(alreadyUsed: 4990, () => June, reserve: 10);
        var held = await quota.TryReserveAsync(default);
        Assert.NotNull(held);
        quota.Complete(held.Value, actualQueries: 14);

        Assert.Equal(5004, await quota.UsedThisMonthAsync(default));   // the overshoot is visible, never hidden
        Assert.Null(await quota.TryReserveAsync(default));              // and nothing more starts this month
    }

    [Fact]
    public async Task The_reviewed_case_4985_used_and_a_16_query_call_never_starts_with_the_default_settings()
    {
        // 4,985 used + the 15 reserved would pass the stopping point (5,000 - 100): the search is refused before any query runs
        var quota = new GroundingQuota((_, _) => Task.FromResult(4985), Options.Create(new GroundedSearchOptions()), () => June);
        Assert.Null(await quota.TryReserveAsync(default));
    }

    [Fact]
    public async Task Searches_stop_a_safety_margin_short_of_the_limit()
    {
        var options = new GroundedSearchOptions();   // limit 5000, reserve 15, margin 100
        var atCeiling = new GroundingQuota((_, _) => Task.FromResult(4885), Options.Create(options), () => June);
        Assert.NotNull(await atCeiling.TryReserveAsync(default));   // 4885 + 15 = 4900: allowed
        var over = new GroundingQuota((_, _) => Task.FromResult(4886), Options.Create(options), () => June);
        Assert.Null(await over.TryReserveAsync(default));           // 4886 + 15 = 4901: refused
    }

    [Fact]
    public async Task A_call_that_overshoots_its_reserve_from_the_ceiling_still_ends_inside_the_limit()
    {
        var quota = new GroundingQuota((_, _) => Task.FromResult(4885), Options.Create(new GroundedSearchOptions()), () => June);
        var held = await quota.TryReserveAsync(default);
        Assert.NotNull(held);
        quota.Complete(held.Value, actualQueries: 16);   // one over its reserve of 15
        var used = await quota.UsedThisMonthAsync(default);
        Assert.Equal(4901, used);
        Assert.True(used <= new GroundedSearchOptions().MonthlyQueryLimit);
        Assert.Null(await quota.TryReserveAsync(default));
    }

    [Theory]
    [InlineData("gemini-3.1-flash-lite", true)]
    [InlineData("gemini-3.5-flash-lite", true)]
    [InlineData("gemini-3.5-flash", false)]
    [InlineData("gemini-3.8-flash", false)]
    public void Only_flash_lite_search_models_are_accepted_unless_a_larger_model_is_explicitly_allowed(string model, bool accepted)
    {
        Assert.Equal(accepted, new GroundedSearchOptions { Model = model }.ModelHasBoundedQueries);
        Assert.True(new GroundedSearchOptions { Model = model, AllowLargerSearchModel = true }.ModelHasBoundedQueries);
    }
}
