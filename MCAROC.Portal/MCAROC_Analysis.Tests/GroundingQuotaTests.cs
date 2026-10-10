using MCAROC_Analysis.Services.GroundedSearch;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class GroundingQuotaTests
{
    private static GroundingQuota Quota(int alreadyUsed, Func<DateTime> now, int limit = 5000, int reserve = 10, List<DateTime>? loads = null) =>
        new((monthStart, _) => { loads?.Add(monthStart); return Task.FromResult(alreadyUsed); },
            Options.Create(new GroundedSearchOptions { SoftCapQueries = limit, HardStopQueries = limit + 100, ReservedQueriesPerCall = reserve }), now);

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
    public async Task The_reviewed_case_4985_used_never_starts_with_the_default_settings()
    {
        var quota = new GroundingQuota((_, _) => Task.FromResult(4985), Options.Create(new GroundedSearchOptions()), () => June);
        Assert.Null(await quota.TryReserveAsync(default));
    }

    [Fact]
    public async Task The_defaults_are_a_working_cap_of_4950_and_a_hard_stop_of_5050()
    {
        var options = new GroundedSearchOptions();
        Assert.Equal((4950, 5050, 15), (options.SoftCapQueries, options.HardStopQueries, options.ReservedQueriesPerCall));
        var atCap = new GroundingQuota((_, _) => Task.FromResult(4935), Options.Create(options), () => June);
        Assert.NotNull(await atCap.TryReserveAsync(default));        // 4935 + 15 = 4950: allowed
        var over = new GroundingQuota((_, _) => Task.FromResult(4936), Options.Create(options), () => June);
        Assert.Null(await over.TryReserveAsync(default));            // 4936 + 15 = 4951: refused
    }

    [Fact]
    public async Task A_call_that_overshoots_its_reserve_ends_between_the_cap_and_the_hard_stop_and_closes_the_month()
    {
        var quota = new GroundingQuota((_, _) => Task.FromResult(4935), Options.Create(new GroundedSearchOptions()), () => June);
        var held = await quota.TryReserveAsync(default);
        Assert.NotNull(held);
        quota.Complete(held.Value, actualQueries: 40);               // far past its reserve of 15
        var used = await quota.UsedThisMonthAsync(default);
        Assert.Equal(4975, used);
        Assert.True(used < new GroundedSearchOptions().HardStopQueries);
        Assert.Null(await quota.TryReserveAsync(default));           // 4975 + 15 > 4950: nothing more starts
    }

    [Fact]
    public async Task The_hard_stop_holds_even_if_the_working_cap_were_set_higher()
    {
        var options = new GroundedSearchOptions { SoftCapQueries = 9999, HardStopQueries = 5050 };   // reserve 15
        var atStop = new GroundingQuota((_, _) => Task.FromResult(5035), Options.Create(options), () => June);
        Assert.NotNull(await atStop.TryReserveAsync(default));       // 5035 + 15 = 5050: allowed
        var past = new GroundingQuota((_, _) => Task.FromResult(5036), Options.Create(options), () => June);
        Assert.Null(await past.TryReserveAsync(default));            // 5036 + 15 = 5051: refused
        var beyond = new GroundingQuota((_, _) => Task.FromResult(5050), Options.Create(options), () => June);
        Assert.Null(await beyond.TryReserveAsync(default));
    }

    [Fact]
    public async Task Searches_in_flight_count_toward_the_hard_stop()
    {
        var options = new GroundedSearchOptions { SoftCapQueries = 9999, HardStopQueries = 5050, ReservedQueriesPerCall = 30 };
        var quota = new GroundingQuota((_, _) => Task.FromResult(5000), Options.Create(options), () => June);
        Assert.NotNull(await quota.TryReserveAsync(default));        // 5000 + 30 held
        Assert.Null(await quota.TryReserveAsync(default));           // 5030 + 30 = 5060 > 5050
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
