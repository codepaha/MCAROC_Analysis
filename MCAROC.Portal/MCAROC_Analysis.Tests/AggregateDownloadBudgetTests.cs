using MCAROC_Analysis.Services.AutoFetch;

namespace MCAROC_Analysis.Tests;

/// <summary>Closes the review finding on PR #224 head <c>0ca705d</c>: the aggregate download cap was a
/// separate check-then-add across concurrent workers (check <c>bytes &gt;= aggregateCap</c> before
/// starting, add the real size only after finishing) — several workers could pass the check in the same
/// window before any of them had added anything, overshooting by up to
/// <c>DownloadConcurrency × MaxResponseBytes</c> instead of one file's worth. These tests hammer
/// <see cref="AggregateDownloadBudget"/> with real concurrent tasks (no artificial delays needed — the
/// invariant must hold under ANY interleaving, not just a timing window that happens to trigger it) and
/// assert the structural bound the fix promises.</summary>
public class AggregateDownloadBudgetTests
{
    [Fact]
    public async Task Concurrent_reservations_never_overshoot_capacity_by_more_than_one_worst_case_unit()
    {
        const long capacity = 1_000_000; // 1 MB
        const long worstCase = 300_000; // 300 KB — mirrors a real MaxResponseBytes
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

        // The old check-then-add design could let ~200 of these through (all racing the same stale read).
        // The fix bounds it to roughly capacity/worstCase, plus at most one more crossing the threshold.
        Assert.True(admitted <= capacity / worstCase + 1, $"Admitted {admitted} reservations, expected at most {capacity / worstCase + 1}.");
        Assert.True(budget.Reserved <= capacity + worstCase, $"Reserved {budget.Reserved}, expected at most {capacity + worstCase} (capacity + one worst-case unit).");
        Assert.True(budget.Reserved >= capacity, "At least one reservation should have crossed the threshold, or capacity was never actually exercised.");
    }

    [Fact]
    public async Task Concurrent_reserve_then_commit_at_smaller_real_sizes_never_exceeds_the_bound_either()
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

        Assert.True(budget.Reserved <= capacity + worstCase, $"Reserved {budget.Reserved}, expected at most {capacity + worstCase}.");
    }

    [Fact]
    public void Release_gives_back_a_reservation_for_a_transfer_that_never_wrote_anything()
    {
        // Admission is "any room at all", not "this exact reservation must fit without crossing the
        // cap" — that is what bounds the overshoot to one unit instead of leaving the last sliver of
        // capacity permanently unusable. So the SECOND 600-byte reservation here is legitimately admitted
        // too (0 -> 600 has room; the check is on the value BEFORE reserving, not after) — capacity is
        // only exhausted once Reserved itself reaches or passes it, which happens on this second call.
        var budget = new AggregateDownloadBudget(capacityBytes: 600);
        Assert.True(budget.TryReserve(600)); // 0 < 600 — admitted; Reserved becomes 600
        Assert.False(budget.TryReserve(600)); // 600 >= 600 — no room left at all, refused

        budget.Release(600); // the first transfer failed and never wrote anything
        Assert.Equal(0, budget.Reserved);
        Assert.True(budget.TryReserve(600)); // room again
    }

    [Fact]
    public void SeedKnownUsage_counts_toward_capacity_without_needing_its_own_reservation()
    {
        var budget = new AggregateDownloadBudget(capacityBytes: 1000);
        budget.SeedKnownUsage(1000); // bytes already staged from a previous, interrupted run — at capacity exactly

        Assert.False(budget.TryReserve(200)); // 1000 >= 1000 — correctly refused, no headroom left at all
        Assert.Equal(1000, budget.Reserved);
    }

    [Fact]
    public void TryReserve_admits_as_long_as_any_room_remains_even_if_the_reservation_itself_crosses_capacity()
    {
        // This is the deliberate, bounded exception to "never exceed capacity": as long as Reserved is
        // strictly under capacity at the moment of the check, one more worst-case-sized reservation is
        // admitted even if it pushes the total over — otherwise the last sliver of a cap could never be
        // used by anything smaller than exactly that sliver. The bound this still guarantees: at most one
        // reservation's worth of overshoot, never more, and never a second one once capacity is reached.
        var budget = new AggregateDownloadBudget(capacityBytes: 100);
        Assert.True(budget.TryReserve(90)); // 0 < 100 — admitted; Reserved becomes 90
        Assert.True(budget.TryReserve(90)); // 90 < 100 — still admitted; Reserved becomes 180, past capacity
        Assert.False(budget.TryReserve(90)); // 180 >= 100 — no further admission
        Assert.Equal(180, budget.Reserved);
    }
}
