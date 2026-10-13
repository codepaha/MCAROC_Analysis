using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class HostTrafficGateTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
        // These tests own the host traffic state and the hosts' permits.
        await db.Set<ModuleHostTrafficState>().ExecuteDeleteAsync();
        await db.OperationalSlotLeases.Where(s => s.SlotType.StartsWith("LitigationHost_")).ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Utc { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    }

    private static readonly HostTrafficOptions Options = new()
    {
        DrtMaxConcurrency = 4, ConsumerMaxConcurrency = 2, FailureThreshold = 3, OpenDuration = TimeSpan.FromMinutes(5), ProbeTimeout = TimeSpan.FromMinutes(2), AdditiveIncrease = 0.25
    };

    /// <summary>Each gate gets its own context, like an agent in its own scope or process.</summary>
    private static HostTrafficGate Gate(AppDbContext db, TestClock clock, HostTrafficOptions? options = null) =>
        new(db, new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance), options ?? Options, clock);

    private static async Task<HostTrafficSnapshot> SnapshotAsync(TestClock clock, FetchHost host = FetchHost.Drt)
    {
        await using var db = Ctx();
        return await Gate(db, clock).SnapshotAsync(host, default);
    }

    private static async Task ReportAsync(TestClock clock, HostCallOutcome outcome, int times = 1, TimeSpan? retryAfter = null, FetchHost host = FetchHost.Drt)
    {
        await using var db = Ctx();
        var gate = Gate(db, clock);
        var permit = new HostPermit(host, "reporter", false);
        for (var i = 0; i < times; i++) await gate.ReportAsync(permit, outcome, "test", retryAfter, default);
    }

    [Fact]
    public async Task The_host_cap_holds_across_agents_and_frees_on_release()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var contexts = Enumerable.Range(0, 3).Select(_ => Ctx()).ToList();
        try
        {
            var a = await Gate(contexts[0], clock).TryEnterAsync(FetchHost.Consumer, "agent-a", default);
            var b = await Gate(contexts[1], clock).TryEnterAsync(FetchHost.Consumer, "agent-b", default);
            var c = await Gate(contexts[2], clock).TryEnterAsync(FetchHost.Consumer, "agent-c", default);
            Assert.True(a.Admitted);
            Assert.True(b.Admitted);
            Assert.False(c.Admitted);                                                 // the consumer portal takes two at once, whoever asks
            Assert.Equal(HostEntryRefusal.AtCapacity, c.Refusal);
            Assert.NotNull(c.RetryAtUtc);

            await Gate(contexts[0], clock).ReleaseAsync(a.Permit!, default);
            Assert.True((await Gate(contexts[2], clock).TryEnterAsync(FetchHost.Consumer, "agent-c", default)).Admitted);
            Assert.True((await Gate(contexts[2], clock).TryEnterAsync(FetchHost.Drt, "agent-c", default)).Admitted);   // another host has its own cap
        }
        finally { foreach (var db in contexts) await db.DisposeAsync(); }
    }

    [Fact]
    public async Task Ten_agents_asking_at_once_get_exactly_the_cap()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var entries = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            await using var db = Ctx();
            return await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "agent-" + i, default);
        }));
        Assert.Equal(4, entries.Count(e => e.Admitted));
    }

    [Fact]
    public async Task A_429_halves_the_concurrency_down_to_the_floor_and_answers_bring_it_back_slowly()
    {
        var clock = new TestClock(DateTime.UtcNow);
        Assert.Equal(4, (await SnapshotAsync(clock)).Concurrency);
        await ReportAsync(clock, HostCallOutcome.Throttled);
        Assert.Equal(2, (await SnapshotAsync(clock)).Concurrency);
        await ReportAsync(clock, HostCallOutcome.Throttled);
        Assert.Equal(1, (await SnapshotAsync(clock)).Concurrency);
        await ReportAsync(clock, HostCallOutcome.Answered);                          // closes what the two 429s counted, but does not reset the cap at once
        await ReportAsync(clock, HostCallOutcome.Throttled);
        Assert.Equal(1, (await SnapshotAsync(clock)).Concurrency);                   // never below the floor

        await ReportAsync(clock, HostCallOutcome.Answered, times: 3);
        Assert.Equal(1, (await SnapshotAsync(clock)).Concurrency);                   // 1.75: not yet a whole extra slot
        await ReportAsync(clock, HostCallOutcome.Answered);
        Assert.Equal(2, (await SnapshotAsync(clock)).Concurrency);
        await ReportAsync(clock, HostCallOutcome.Answered, times: 20);
        Assert.Equal(4, (await SnapshotAsync(clock)).Concurrency);                   // back to the agreed maximum, never above it

        // and the lowered cap is what admission uses
        await ReportAsync(clock, HostCallOutcome.Throttled);
        await ReportAsync(clock, HostCallOutcome.Answered);                          // keep the breaker closed
        await using var db = Ctx();
        var gate = Gate(db, clock);
        var admitted = 0;
        for (var i = 0; i < 4; i++) if ((await gate.TryEnterAsync(FetchHost.Drt, "agent-" + i, default)).Admitted) admitted++;
        Assert.Equal(2, admitted);
    }

    [Fact]
    public async Task The_breaker_opens_after_the_threshold_and_admits_nobody_until_the_open_period_ends()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 2);
        Assert.False((await SnapshotAsync(clock)).BreakerOpen);
        await ReportAsync(clock, HostCallOutcome.Answered);                          // an answer in between resets the count: failures must be consecutive
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 2);
        Assert.False((await SnapshotAsync(clock)).BreakerOpen);
        await ReportAsync(clock, HostCallOutcome.Unavailable);
        var snap = await SnapshotAsync(clock);
        Assert.True(snap.BreakerOpen);
        Assert.Equal(3, snap.ConsecutiveFailures);

        await using var db = Ctx();
        var entry = await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "agent", default);
        Assert.False(entry.Admitted);
        Assert.Equal(HostEntryRefusal.BreakerOpen, entry.Refusal);
        Assert.Equal(snap.OpenUntilUtc, entry.RetryAtUtc);                           // the agent defers the item to the end of the open period

        Assert.True((await Gate(db, clock).TryEnterAsync(FetchHost.Consumer, "agent", default)).Admitted);   // the other portal is unaffected
    }

    [Fact]
    public async Task After_the_open_period_one_probe_goes_and_its_answer_closes_the_breaker()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 3);
        clock.Utc += TimeSpan.FromMinutes(6);

        await using var db1 = Ctx();
        await using var db2 = Ctx();
        var probe = await Gate(db1, clock).TryEnterAsync(FetchHost.Drt, "agent-1", default);
        var second = await Gate(db2, clock).TryEnterAsync(FetchHost.Drt, "agent-2", default);
        Assert.True(probe.Admitted);
        Assert.True(probe.Permit!.IsProbe);
        Assert.False(second.Admitted);                                               // half-open lets exactly one through
        Assert.Equal(HostEntryRefusal.BreakerOpen, second.Refusal);

        await Gate(db1, clock).ReportAsync(probe.Permit, HostCallOutcome.Answered, null, null, default);
        await Gate(db1, clock).ReleaseAsync(probe.Permit, default);
        var snap = await SnapshotAsync(clock);
        Assert.False(snap.BreakerOpen);
        Assert.Null(snap.OpenUntilUtc);
        var after = await Gate(db2, clock).TryEnterAsync(FetchHost.Drt, "agent-2", default);
        Assert.True(after.Admitted);
        Assert.False(after.Permit!.IsProbe);
    }

    [Fact]
    public async Task A_failed_probe_opens_the_breaker_again_at_once()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 3);
        clock.Utc += TimeSpan.FromMinutes(6);
        await using var db = Ctx();
        var gate = Gate(db, clock);
        var probe = await gate.TryEnterAsync(FetchHost.Drt, "agent", default);
        Assert.True(probe.Permit!.IsProbe);

        await gate.ReportAsync(probe.Permit, HostCallOutcome.Unavailable, "503", null, default);
        await gate.ReleaseAsync(probe.Permit, default);
        var snap = await SnapshotAsync(clock);
        Assert.True(snap.BreakerOpen);
        Assert.Equal(clock.Utc + TimeSpan.FromMinutes(5), snap.OpenUntilUtc);
        Assert.False((await gate.TryEnterAsync(FetchHost.Drt, "agent", default)).Admitted);
    }

    [Fact]
    public async Task A_prober_that_dies_does_not_keep_the_breaker_half_open_forever()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 3);
        clock.Utc += TimeSpan.FromMinutes(6);
        await using var db = Ctx();
        var dead = await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "dead-agent", default);
        Assert.True(dead.Permit!.IsProbe);                                           // and it never reports or releases

        Assert.False((await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "agent-2", default)).Admitted);
        clock.Utc += TimeSpan.FromMinutes(3);                                        // past the probe timeout
        var next = await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "agent-2", default);
        Assert.True(next.Admitted);
        Assert.True(next.Permit!.IsProbe);
    }

    [Fact]
    public async Task Retry_After_from_the_host_keeps_everyone_away_for_that_long()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await ReportAsync(clock, HostCallOutcome.Throttled, retryAfter: TimeSpan.FromMinutes(20));    // one 429, below the threshold, but the host said when to come back
        var snap = await SnapshotAsync(clock);
        Assert.True(snap.BreakerOpen);
        Assert.Equal(clock.Utc + TimeSpan.FromMinutes(20), snap.OpenUntilUtc);
        clock.Utc += TimeSpan.FromMinutes(10);
        await using var db = Ctx();
        Assert.False((await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "agent", default)).Admitted);
    }

    [Fact]
    public async Task Reports_from_many_agents_at_once_are_all_counted()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var wide = Options with { FailureThreshold = 1000 };
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await using var db = Ctx();
            await Gate(db, clock, wide).ReportAsync(new HostPermit(FetchHost.Drt, "agent-" + i, false), HostCallOutcome.Unavailable, null, null, default);
        }));
        Assert.Equal(20, (await SnapshotAsync(clock)).ConsecutiveFailures);
    }

    [Fact]
    public async Task Calls_that_never_reached_the_host_do_not_move_its_state()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await SnapshotAsync(clock);
        await ReportAsync(clock, HostCallOutcome.NotSent, times: 10);
        var snap = await SnapshotAsync(clock);
        Assert.Equal(0, snap.ConsecutiveFailures);
        Assert.False(snap.BreakerOpen);
        Assert.Null(snap.LastSuccessUtc);
    }

    [Fact]
    public async Task An_answer_from_a_call_already_in_flight_does_not_close_an_open_breaker()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var inFlight = await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "slow-agent", default);    // admitted while the host was healthy
        Assert.True(inFlight.Admitted);
        await ReportAsync(clock, HostCallOutcome.Unavailable, times: 3);                                // meanwhile the breaker opens
        await Gate(db, clock).ReportAsync(inFlight.Permit!, HostCallOutcome.Answered, null, null, default);   // then the old call comes back fine
        var snap = await SnapshotAsync(clock);
        Assert.True(snap.BreakerOpen);
        Assert.Equal(3, snap.ConsecutiveFailures);

        // nor in half-open: only the probe closes it
        clock.Utc += TimeSpan.FromMinutes(6);
        var probe = await Gate(db, clock).TryEnterAsync(FetchHost.Drt, "prober", default);
        Assert.True(probe.Permit!.IsProbe);
        await Gate(db, clock).ReportAsync(inFlight.Permit!, HostCallOutcome.Answered, null, null, default);
        Assert.NotNull((await SnapshotAsync(clock)).OpenUntilUtc);
        await Gate(db, clock).ReportAsync(probe.Permit, HostCallOutcome.Answered, null, null, default);
        Assert.Null((await SnapshotAsync(clock)).OpenUntilUtc);
    }

    /// <summary>A lease service that lets something happen between the gate's state read and the lease, like a report by another agent at that moment.</summary>
    private sealed class InterleavingSlots(IOperationalSlotLeaseService inner, Func<Task> before) : IOperationalSlotLeaseService
    {
        public async Task<SlotLeaseResult> TryAcquireSlotAsync(string slotType, string holderId, TimeSpan duration, int capacity = 1, CancellationToken ct = default)
        {
            await before();
            return await inner.TryAcquireSlotAsync(slotType, holderId, duration, capacity, ct);
        }
        public Task<bool> TryRenewSlotAsync(string slotType, string holderId, TimeSpan extension, CancellationToken ct = default) => inner.TryRenewSlotAsync(slotType, holderId, extension, ct);
        public Task ReleaseSlotAsync(string slotType, string holderId, CancellationToken ct = default) => inner.ReleaseSlotAsync(slotType, holderId, ct);
    }

    [Fact]
    public async Task A_429_between_the_state_read_and_the_lease_is_honoured()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var gate = Gate(db, clock);
        for (var i = 0; i < 2; i++) Assert.True((await gate.TryEnterAsync(FetchHost.Drt, "agent-" + i, default)).Admitted);   // 2 of 4 held

        var inner = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var racing = new HostTrafficGate(db, new InterleavingSlots(inner, () => ReportAsync(clock, HostCallOutcome.Throttled)), Options, clock);
        var entry = await racing.TryEnterAsync(FetchHost.Drt, "late-agent", default);   // the read said 4; a 429 halves it to 2 before the lease

        Assert.False(entry.Admitted);
        Assert.Equal(HostEntryRefusal.AtCapacity, entry.Refusal);
        Assert.Equal(2, await db.OperationalSlotLeases.CountAsync(l => l.SlotType == "LitigationHost_Drt"));   // the late permit was given back
    }

    [Fact]
    public async Task A_breaker_opening_between_the_state_read_and_the_lease_is_honoured()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var inner = new OperationalSlotLeaseService(db, NullLogger<OperationalSlotLeaseService>.Instance);
        var racing = new HostTrafficGate(db, new InterleavingSlots(inner, () => ReportAsync(clock, HostCallOutcome.Unavailable, times: 3)), Options, clock);
        var entry = await racing.TryEnterAsync(FetchHost.Drt, "late-agent", default);
        Assert.False(entry.Admitted);
        Assert.Equal(HostEntryRefusal.BreakerOpen, entry.Refusal);
        Assert.Equal(0, await db.OperationalSlotLeases.CountAsync(l => l.SlotType == "LitigationHost_Drt"));
    }
}
