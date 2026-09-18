using MCAROC_Analysis.Services.AutoFetch;

namespace MCAROC_Analysis.Tests;

/// <summary>Closes two rounds of the same review finding on PR #224.
/// Round 1 (head <c>0ca705d</c>): the aggregate download cap was a separate check-then-add across
/// concurrent workers (check <c>bytes &gt;= aggregateCap</c> before starting, add the real size only after
/// finishing) — several workers could pass the check in the same window before any of them had added
/// anything, overshooting by up to <c>DownloadConcurrency × MaxResponseBytes</c>.
/// Round 2 (head <c>be5ca4a</c>): admission was made atomic, but still admitted whenever
/// <c>current &lt; capacity</c> regardless of whether the reservation itself would cross the cap — bounding
/// the overshoot to one transfer's worth, but <c>MaxAggregateDownloadBytes</c> is a configured limit, not
/// an approximate one, and configuring it to 20 GiB and then silently allowing up to 20 GiB +
/// <c>MaxResponseBytes</c> is still exceeding it.
/// <see cref="AggregateDownloadBudget"/> is now a genuine hard cap: <see cref="AggregateDownloadBudget.Reserved"/>
/// never exceeds capacity as a result of <see cref="AggregateDownloadBudget.TryReserve"/> alone. These
/// tests hammer it with real concurrent tasks (no artificial delays needed for the atomicity proof — the
/// invariant must hold under ANY interleaving, not just a timing window that happens to trigger it).</summary>
public class AggregateDownloadBudgetTests
{
    [Fact]
    public async Task Concurrent_reservations_never_exceed_capacity_even_slightly()
    {
        const long capacity = 1_000_000; // 1 MB
        const long worstCase = 300_000; // 300 KB — mirrors a real MaxResponseBytes; not an exact divisor of capacity
        var budget = new AggregateDownloadBudget(capacity);

        // Far more concurrent attempts than could ever fit — if admission were racy, most of these would
        // slip through before any commit/release happened.
        var admitted = 0;
        var tasks = Enumerable.Range(0, 200).Select(_ => Task.Run(() =>
        {
            if (budget.TryReserve(worstCase))
                Interlocked.Increment(ref admitted);
        }));
        await Task.WhenAll(tasks);

        // Exactly floor(capacity / worstCase) = 3 fit (3 × 300,000 = 900,000 ≤ 1,000,000); a 4th would be
        // 1,200,000 > 1,000,000 and must be refused, not "admitted because 900,000 < 1,000,000 still had
        // room" — that was round 2's bug. The remaining 100,000 of capacity goes deliberately unused.
        Assert.Equal(3, admitted);
        Assert.Equal(900_000, budget.Reserved);
        Assert.True(budget.Reserved <= capacity, $"Reserved {budget.Reserved} must never exceed capacity {capacity}.");
    }

    [Fact]
    public async Task Concurrent_reserve_then_commit_at_smaller_real_sizes_never_exceeds_capacity()
    {
        const long capacity = 2_000_000;
        const long worstCase = 500_000;
        var budget = new AggregateDownloadBudget(capacity);

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            if (!budget.TryReserve(worstCase)) return;
            await Task.Delay(Random.Shared.Next(1, 5)); // simulate a real transfer's varying duration
            var actualSize = worstCase / 2; // every real file comes in under the worst-case cap, as it always does
            budget.Commit(worstCase, actualSize);
        }));
        await Task.WhenAll(tasks);

        // Commits below the worst case free room for MORE admissions over time (by design — a company
        // whose real files run smaller than the per-file cap should be able to fetch more of them within
        // the same aggregate budget), but the running total must never cross capacity at any point to get
        // there.
        Assert.True(budget.Reserved <= capacity, $"Reserved {budget.Reserved} must never exceed capacity {capacity}.");
    }

    [Fact]
    public void Release_gives_back_a_reservation_for_a_transfer_that_never_wrote_anything()
    {
        var budget = new AggregateDownloadBudget(capacityBytes: 600);
        Assert.True(budget.TryReserve(600)); // 0 + 600 <= 600 — admitted, exactly at capacity
        Assert.False(budget.TryReserve(600)); // 600 + 600 > 600 — refused, no room left at all

        budget.Release(600); // the first transfer failed and never wrote anything
        Assert.Equal(0, budget.Reserved);
        Assert.True(budget.TryReserve(600)); // room again
    }

    [Fact]
    public void SeedKnownUsage_counts_toward_capacity_without_needing_its_own_reservation()
    {
        var budget = new AggregateDownloadBudget(capacityBytes: 1000);
        budget.SeedKnownUsage(1000); // bytes already staged from a previous, interrupted run — at capacity exactly

        Assert.False(budget.TryReserve(200)); // 1000 + 200 > 1000 — correctly refused, no headroom left at all
        Assert.Equal(1000, budget.Reserved);
    }

    [Fact]
    public void TryReserve_refuses_a_reservation_that_would_cross_capacity_even_with_room_still_left()
    {
        // The exact scenario round 2 got wrong: 10 bytes of headroom remain (90 of 100 used), but a
        // 90-byte reservation would need all of it and then some — it must be refused outright, not
        // admitted because SOME room existed. Contrast with SeedKnownUsage's test above and
        // Release's, where the reservation exactly fits the remaining capacity and IS admitted.
        var budget = new AggregateDownloadBudget(capacityBytes: 100);
        Assert.True(budget.TryReserve(90)); // 0 + 90 <= 100 — admitted; Reserved becomes 90
        Assert.False(budget.TryReserve(90)); // 90 + 90 = 180 > 100 — refused; 10 bytes of capacity go unused
        Assert.Equal(90, budget.Reserved);

        // A reservation that fits the remaining 10 bytes exactly is still admitted — the cap refuses an
        // over-large request, not all further requests once anything is reserved.
        Assert.True(budget.TryReserve(10));
        Assert.Equal(100, budget.Reserved);
        Assert.False(budget.TryReserve(1)); // now genuinely full
    }
}
