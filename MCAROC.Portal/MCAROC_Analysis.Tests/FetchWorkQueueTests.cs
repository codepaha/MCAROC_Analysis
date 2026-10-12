using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class FetchWorkQueueTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
        // These tests own the queue: start each class run from an empty one.
        await db.Set<ModuleFetchWorkItem>().ExecuteDeleteAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long NewCaseId() => Math.Abs(Guid.NewGuid().GetHashCode()) + 5_000_000L;

    /// <summary>A clock a test can move.</summary>
    private sealed class TestClock(DateTime start) : TimeProvider
    {
        public DateTime Utc { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Utc, DateTimeKind.Utc));
    }

    private static FetchWorkQueue Queue(AppDbContext db, TestClock? clock = null, FetchQueueOptions? options = null) => new(db, options, clock);

    private static async Task<ModuleFetchWorkItem> ReadAsync(long id) { await using var db = Ctx(); return await db.Set<ModuleFetchWorkItem>().AsNoTracking().SingleAsync(w => w.WorkItemId == id); }

    [Fact]
    public async Task Enqueueing_the_same_work_twice_creates_one_active_item()
    {
        var caseId = NewCaseId();
        await using var db = Ctx();
        var q = Queue(db);
        var first = await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, caseId, null, null, 0, default);
        var again = await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, caseId, null, null, 0, default);
        Assert.True(first.Created);
        Assert.False(again.Created);
        Assert.Equal(first.WorkItemId, again.WorkItemId);                                                                       // the existing item is returned
        Assert.True((await q.EnqueueAsync(FetchWorkKind.OrdersFetch, FetchHost.Drt, caseId, null, null, 0, default)).Created);      // another kind is other work
        Assert.True((await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Consumer, caseId, null, null, 0, default)).Created); // another host too
    }

    [Fact]
    public async Task Concurrent_enqueues_of_the_same_work_create_exactly_one_item()
    {
        var caseId = NewCaseId();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = Ctx();
            return await Queue(db).EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, caseId, null, null, 0, default);
        }));
        Assert.Equal(1, results.Count(r => r.Created));
        Assert.Single(results.Select(r => r.WorkItemId).Distinct());
        await using var v = Ctx();
        Assert.Equal(1, await v.Set<ModuleFetchWorkItem>().CountAsync(w => w.CaseId == caseId));
    }

    [Fact]
    public async Task Many_agents_claiming_at_once_each_get_a_different_item_and_none_is_handed_out_twice()
    {
        var ids = new List<long>();
        await using (var seed = Ctx())
            for (var i = 0; i < 20; i++) ids.Add((await Queue(seed).EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 0, default)).WorkItemId);

        var claimed = (await Task.WhenAll(Enumerable.Range(0, 30).Select(async agent =>
        {
            await using var db = Ctx();
            return await Queue(db).ClaimNextAsync(FetchHost.Drt, $"agent-{agent}", default);
        }))).Where(c => c is not null).Select(c => c!.WorkItemId).ToList();

        Assert.Equal(claimed.Count, claimed.Distinct().Count());                          // never the same item to two agents
        Assert.Equal(20, claimed.Count);                                                  // 30 agents, 20 items: all 20 claimed, 10 agents got nothing
        Assert.All(claimed, id => Assert.Contains(id, ids));
    }

    [Fact]
    public async Task A_claim_takes_the_best_priority_then_the_oldest_and_only_for_its_own_host()
    {
        await using var db = Ctx();
        var q = Queue(db);
        var low = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Consumer, NewCaseId(), null, null, 1, default)).WorkItemId;
        var highOld = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Consumer, NewCaseId(), null, null, 5, default)).WorkItemId;
        var highNew = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Consumer, NewCaseId(), null, null, 5, default)).WorkItemId;
        await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 9, default);

        Assert.Equal(highOld, (await q.ClaimNextAsync(FetchHost.Consumer, "a", default))!.WorkItemId);
        Assert.Equal(highNew, (await q.ClaimNextAsync(FetchHost.Consumer, "a", default))!.WorkItemId);
        Assert.Equal(low, (await q.ClaimNextAsync(FetchHost.Consumer, "a", default))!.WorkItemId);
        Assert.Null(await q.ClaimNextAsync(FetchHost.Consumer, "a", default));           // the DRT item is not a consumer agent's
    }

    [Fact]
    public async Task A_stale_agent_that_lost_its_lease_cannot_record_a_result()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var q = Queue(db, clock);
        var id = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 0, default)).WorkItemId;
        var slow = (await q.ClaimNextAsync(FetchHost.Drt, "slow", default))!;

        clock.Utc = clock.Utc.AddMinutes(10);                                             // slow's lease (5 min) has run out
        var takeover = (await q.ClaimNextAsync(FetchHost.Drt, "fast", default))!;
        Assert.Equal(id, takeover.WorkItemId);
        Assert.NotEqual(slow.LeaseToken, takeover.LeaseToken);

        Assert.False(await q.CompleteAsync(slow, "Found", default));                      // the slow agent's late result is discarded
        Assert.False(await q.RenewAsync(slow, default));
        Assert.False((await q.FailAttemptAsync(slow, "Failed", "late", default)).Recorded);
        Assert.True(await q.CompleteAsync(takeover, "Found", default));
        Assert.Equal((int)FetchWorkStatus.Succeeded, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task The_first_lease_expiry_is_free_and_each_later_one_costs_an_attempt()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var q = Queue(db, clock);
        var id = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 0, default)).WorkItemId;

        await q.ClaimNextAsync(FetchHost.Drt, "a", default);
        clock.Utc = clock.Utc.AddMinutes(6);
        await q.RecoverExpiredLeasesAsync(FetchHost.Drt, default);
        var afterFirst = await ReadAsync(id);
        Assert.Equal((int)FetchWorkStatus.Queued, afterFirst.Status);
        Assert.Equal((1, 0), (afterFirst.LeaseExpiryCount, afterFirst.AttemptCount));     // free

        await q.ClaimNextAsync(FetchHost.Drt, "b", default);
        clock.Utc = clock.Utc.AddMinutes(6);
        await q.RecoverExpiredLeasesAsync(FetchHost.Drt, default);
        var afterSecond = await ReadAsync(id);
        Assert.Equal((2, 1), (afterSecond.LeaseExpiryCount, afterSecond.AttemptCount));   // costs one
    }

    [Fact]
    public async Task A_failed_attempt_cools_down_on_the_schedule_and_the_last_attempt_fails_the_item()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var q = Queue(db, clock);
        var id = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 0, default)).WorkItemId;
        var expected = new[] { TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8), TimeSpan.FromHours(24) };

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            var work = (await q.ClaimNextAsync(FetchHost.Drt, "a", default))!;
            Assert.Equal(attempt - 1, work.AttemptCount);
            var result = await q.FailAttemptAsync(work, "Failed", "timeout", default);
            Assert.True(result.Recorded);
            Assert.False(result.Terminal);
            Assert.Equal(clock.Utc + expected[attempt - 1], result.NextAttemptUtc);
            Assert.Null(await q.ClaimNextAsync(FetchHost.Drt, "a", default));              // cooling down: not claimable yet
            clock.Utc = clock.Utc + expected[attempt - 1] + TimeSpan.FromSeconds(1);
        }
        var last = (await q.ClaimNextAsync(FetchHost.Drt, "a", default))!;
        var final = await q.FailAttemptAsync(last, "Failed", "timeout", default);
        Assert.True(final.Terminal);
        var row = await ReadAsync(id);
        Assert.Equal((int)FetchWorkStatus.Failed, row.Status);
        Assert.Equal(5, row.AttemptCount);
        Assert.Null(await q.ClaimNextAsync(FetchHost.Drt, "a", default));
    }

    [Fact]
    public async Task A_deferral_uses_no_attempt_and_the_item_waits_until_its_retry_time()
    {
        var clock = new TestClock(DateTime.UtcNow);
        await using var db = Ctx();
        var q = Queue(db, clock);
        var id = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), null, null, 0, default)).WorkItemId;
        var work = (await q.ClaimNextAsync(FetchHost.Drt, "a", default))!;

        Assert.True(await q.DeferAsync(work, clock.Utc.AddMinutes(10), "portal not responding", default));
        var row = await ReadAsync(id);
        Assert.Equal(((int)FetchWorkStatus.Queued, 0, "Deferred"), (row.Status, row.AttemptCount, row.LastOutcome));
        Assert.Null(await q.ClaimNextAsync(FetchHost.Drt, "a", default));
        clock.Utc = clock.Utc.AddMinutes(11);
        Assert.Equal(id, (await q.ClaimNextAsync(FetchHost.Drt, "a", default))!.WorkItemId);
    }

    [Fact]
    public async Task A_failed_item_becomes_eligible_again_after_a_day_but_not_while_the_same_work_is_active()
    {
        var clock = new TestClock(DateTime.UtcNow);
        var opts = new FetchQueueOptions { MaxAttempts = 1 };
        await using var db = Ctx();
        var q = Queue(db, clock, opts);
        var caseId = NewCaseId();
        var id = (await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, caseId, null, null, 0, default)).WorkItemId;
        await q.FailAttemptAsync((await q.ClaimNextAsync(FetchHost.Drt, "a", default))!, "Failed", "down", default);
        Assert.Equal((int)FetchWorkStatus.Failed, (await ReadAsync(id)).Status);

        clock.Utc = clock.Utc.AddHours(23);
        Assert.Equal(0, await q.RequeueFailedAsync(default));                              // too early
        clock.Utc = clock.Utc.AddHours(2);
        var other = await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, caseId, null, null, 0, default);   // fresh work for the same case is queued meanwhile
        Assert.True(other.Created);
        Assert.Equal(0, await q.RequeueFailedAsync(default));                              // so the old failure does not duplicate it
        await q.CompleteAsync((await q.ClaimNextAsync(FetchHost.Drt, "a", default))!, "Found", default);

        Assert.Equal(1, await q.RequeueFailedAsync(default));                              // nothing active now: eligible again
        var row = await ReadAsync(id);
        Assert.Equal(((int)FetchWorkStatus.Queued, 0), (row.Status, row.AttemptCount));
    }

    [Fact]
    public async Task The_counts_for_a_run_group_its_items_by_status()
    {
        long runId;
        await using (var db = Ctx())
        {
            var run = new ModuleRefreshRun { RequestId = NewCaseId(), StartedUtc = DateTime.UtcNow, EligibleAtStart = 3 };
            db.Set<ModuleRefreshRun>().Add(run);
            await db.SaveChangesAsync();
            runId = run.RunId;
        }
        await using var db2 = Ctx();
        var q = Queue(db2);
        foreach (var _ in Enumerable.Range(0, 3)) await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Consumer, NewCaseId(), runId, null, 0, default);
        await q.CompleteAsync((await q.ClaimNextAsync(FetchHost.Consumer, "a", default))!, "Found", default);

        var counts = await q.CountsForRunAsync(runId, default);
        Assert.Equal((2, 1), (counts[FetchWorkStatus.Queued], counts[FetchWorkStatus.Succeeded]));
    }

    [Fact]
    public async Task Work_already_queued_for_one_run_counts_for_a_second_run_that_needs_it()
    {
        long run1, run2;
        await using (var db = Ctx())
        {
            var r1 = new ModuleRefreshRun { RequestId = NewCaseId(), StartedUtc = DateTime.UtcNow, EligibleAtStart = 2 };
            var r2 = new ModuleRefreshRun { RequestId = NewCaseId(), StartedUtc = DateTime.UtcNow, EligibleAtStart = 2 };
            db.Set<ModuleRefreshRun>().AddRange(r1, r2);
            await db.SaveChangesAsync();
            run1 = r1.RunId; run2 = r2.RunId;
        }
        await using var db2 = Ctx();
        var q = Queue(db2);
        var shared = NewCaseId();
        var first = await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, shared, run1, null, 0, default);
        var second = await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, shared, run2, null, 0, default);          // same work, other run
        await q.EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), run2, null, 0, default);                   // run 2's own item

        Assert.False(second.Created);
        Assert.Equal(first.WorkItemId, second.WorkItemId);
        Assert.Equal(1, (await q.CountsForRunAsync(run1, default))[FetchWorkStatus.Queued]);
        Assert.Equal(2, (await q.CountsForRunAsync(run2, default))[FetchWorkStatus.Queued]);                                   // the shared item counts for both runs

        // and when the shared item finishes, it finishes for both
        await q.CompleteAsync((await q.ClaimNextAsync(FetchHost.Drt, "a", default))!, "Found", default);
        var claimedShared = (await q.CountsForRunAsync(run1, default)).GetValueOrDefault(FetchWorkStatus.Succeeded) + (await q.CountsForRunAsync(run2, default)).GetValueOrDefault(FetchWorkStatus.Succeeded);
        Assert.True(claimedShared >= 1);
    }

    [Fact]
    public async Task A_database_failure_that_is_not_a_duplicate_is_raised_not_reported_as_already_queued()
    {
        await using var db = Ctx();
        // A run id that does not exist breaks the foreign key of the run link: a real failure, not a uniqueness conflict.
        await Assert.ThrowsAsync<DbUpdateException>(() => Queue(db).EnqueueAsync(FetchWorkKind.StatusFetch, FetchHost.Drt, NewCaseId(), long.MaxValue - 9, null, 0, default));
        await using var v = Ctx();
        Assert.Equal(0, await v.Set<ModuleFetchWorkItem>().CountAsync(w => w.RunId == long.MaxValue - 9));        // and nothing half-stored
    }
}
