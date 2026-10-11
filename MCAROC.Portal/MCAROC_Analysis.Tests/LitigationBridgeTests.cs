using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationBridgeTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private static readonly LitigationBridgeOptions On = new() { Enabled = true, BatchSize = 100, MaxAttempts = 2, SyncingBacklogThreshold = 3 };

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<(long RequestId, long[] CaseIds)> SeedLegacyAsync(int cases, bool withOutbox)
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "BR" + Guid.NewGuid().ToString("N")[..8], ClientName = "Bridge Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Bridge Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"BR-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var list = Enumerable.Range(1, cases).Select(i => new LitigationCase
        {
            RequestId = request.RequestId, CaseNumber = i.ToString(), CaseType = "OA", CaseYear = "2022", Court = "DRT Mumbai 1", CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        }).ToList();
        db.LitigationCases.AddRange(list);
        if (withOutbox) foreach (var c in list) LitigationBridgeWriter.RecordCaseUpserted(db, c, On, DateTime.UtcNow);
        await db.SaveChangesAsync();
        return (request.RequestId, list.Select(c => c.LitigationCaseId).ToArray());
    }

    private static async Task<(int Cases, int Links, string Aliases)> ModuleStateAsync(long requestId)
    {
        await using var db = Ctx();
        var scopeId = await db.Set<ModuleScope>().AsNoTracking().Where(s => s.Kind == ModuleScopeKind.Request && s.ExternalId == requestId).Select(s => (long?)s.ScopeId).FirstOrDefaultAsync();
        var links = await db.Set<ModuleScopeCase>().AsNoTracking().Where(x => x.ScopeId == scopeId).OrderBy(x => x.AliasIndex).ToListAsync();
        return (links.Count, links.Count, string.Join(",", links.Select(l => l.Alias)));
    }

    [Fact]
    public async Task Events_create_the_module_case_scope_subject_and_aliases()
    {
        var (requestId, ids) = await SeedLegacyAsync(3, withOutbox: true);
        await using var db = Ctx();
        var result = await new LitigationBridgeProcessor(db, On).ProcessBatchAsync(default);

        Assert.True(result.Applied >= 3);
        var (cases, _, aliases) = await ModuleStateAsync(requestId);
        Assert.Equal(3, cases);
        Assert.Equal("C-1,C-2,C-3", aliases);
        await using var verify = Ctx();
        var scope = await verify.Set<ModuleScope>().AsNoTracking().Include(s => s.Subject).FirstAsync(s => s.Kind == ModuleScopeKind.Request && s.ExternalId == requestId);
        Assert.Equal("Bridge Test Company", scope.Subject!.Name);
        Assert.Equal(3, await verify.Set<ModuleCase>().CountAsync(c => c.LegacyLitigationCaseId != null && ids.Contains(c.LegacyLitigationCaseId.Value)));
    }

    [Fact]
    public async Task Replaying_the_same_events_changes_nothing()
    {
        var (requestId, ids) = await SeedLegacyAsync(3, withOutbox: true);
        await using (var db = Ctx()) await new LitigationBridgeProcessor(db, On).ProcessBatchAsync(default);
        var first = await ModuleStateAsync(requestId);

        // Re-queue events for the same cases, as a crash-and-retry or a second writer would.
        await using (var db = Ctx())
        {
            foreach (var id in ids) db.Set<ModuleBridgeOutbox>().Add(new ModuleBridgeOutbox { Kind = BridgeEventKind.CaseUpserted, LegacyLitigationCaseId = id, RequestId = requestId, CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await using (var db = Ctx()) await new LitigationBridgeProcessor(db, On).ProcessBatchAsync(default);

        Assert.Equal(first, await ModuleStateAsync(requestId));
        await using var verify = Ctx();
        Assert.Equal(3, await verify.Set<ModuleCase>().CountAsync(c => c.LegacyLitigationCaseId != null && ids.Contains(c.LegacyLitigationCaseId.Value)));
    }

    [Fact]
    public async Task Several_events_for_one_case_apply_once_and_all_are_marked_processed()
    {
        var (requestId, ids) = await SeedLegacyAsync(1, withOutbox: true);
        await using (var db = Ctx())
        {
            for (var i = 0; i < 3; i++) db.Set<ModuleBridgeOutbox>().Add(new ModuleBridgeOutbox { Kind = BridgeEventKind.CaseUpserted, LegacyLitigationCaseId = ids[0], RequestId = requestId, CreatedUtc = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        await using var run = Ctx();
        await new LitigationBridgeProcessor(run, On).ProcessBatchAsync(default);
        await using var verify = Ctx();
        Assert.Equal(0, await verify.Set<ModuleBridgeOutbox>().CountAsync(o => o.LegacyLitigationCaseId == ids[0] && o.ProcessedUtc == null));
        Assert.Equal(1, (await ModuleStateAsync(requestId)).Cases);
    }

    [Fact]
    public async Task A_failing_event_does_not_block_the_others_and_is_dead_lettered_after_its_attempts()
    {
        var (requestId, ids) = await SeedLegacyAsync(2, withOutbox: true);
        // Poison: an alias the scope's counter will hand out next is already taken by a different case, so adding the second case can never succeed.
        await using (var db = Ctx())
        {
            var store = new LitigationModuleStore(db);
            var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Bridge Test Company", null, default);
            var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, requestId, subject.SubjectId, default);
            var taken = await store.EnsureCaseForLegacyAsync(long.MaxValue - 11, default);
            await store.AddCaseToScopeAsync(scope.ScopeId, taken.CaseId, "C-2", default);   // counter is now past C-2, so give back the counter to force a clash
            await db.Set<ModuleScope>().Where(s => s.ScopeId == scope.ScopeId).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAliasIndex, 1));
        }

        // First case takes C-1 and works; the second would take C-2, which is taken: it fails every time.
        for (var pass = 0; pass < 3; pass++)
        {
            await using var db = Ctx();
            await new LitigationBridgeProcessor(db, On).ProcessBatchAsync(default);
        }

        await using var verify = Ctx();
        var first = await verify.Set<ModuleBridgeOutbox>().AsNoTracking().Where(o => o.LegacyLitigationCaseId == ids[0]).ToListAsync();
        var second = await verify.Set<ModuleBridgeOutbox>().AsNoTracking().Where(o => o.LegacyLitigationCaseId == ids[1]).ToListAsync();
        Assert.All(first, o => Assert.NotNull(o.ProcessedUtc));
        Assert.All(second, o => { Assert.Null(o.ProcessedUtc); Assert.NotNull(o.DeadLetteredUtc); Assert.Equal(2, o.Attempts); Assert.False(string.IsNullOrEmpty(o.LastError)); });
        var lag = await new LitigationBridgeProcessor(verify, On).GetLagAsync(default);
        Assert.True(lag.DeadLettered >= 1);
        // A dead-lettered event is a case that never reached the module: the request must not look ready.
        Assert.Equal(BridgeReadiness.Failed, await new LitigationBridgeProcessor(verify, On).GetReadinessAsync(requestId, default));
        Assert.True(await new LitigationBridgeProcessor(verify, On).IsSyncingAsync(requestId, default));
    }

    [Fact]
    public async Task A_case_deleted_before_its_event_is_applied_is_skipped_without_error()
    {
        var (requestId, ids) = await SeedLegacyAsync(1, withOutbox: false);
        await using var db = Ctx();
        // The event row cascades away with its case, so a deleted case leaves no event; applying a stale id directly is a no-op.
        var processor = new LitigationBridgeProcessor(db, On);
        Assert.False(await processor.ApplyAsync(long.MaxValue - 5, DateTime.UtcNow, default));
        Assert.Equal(0, (await ModuleStateAsync(requestId)).Cases);
        Assert.Single(ids);
    }

    [Fact]
    public async Task Lag_and_the_syncing_flag_follow_the_pending_backlog()
    {
        var (requestId, _) = await SeedLegacyAsync(5, withOutbox: true);   // threshold in the test options is 3
        await using var db = Ctx();
        var processor = new LitigationBridgeProcessor(db, On);
        Assert.True(await processor.IsSyncingAsync(requestId, default));
        Assert.True((await processor.GetLagAsync(default)).Pending >= 5);

        await processor.ProcessBatchAsync(default);
        await using var after = Ctx();
        Assert.False(await new LitigationBridgeProcessor(after, On).IsSyncingAsync(requestId, default));
    }

    [Fact]
    public async Task The_bridge_off_records_no_events()
    {
        await using var db = Ctx();
        var legacy = new LitigationCase { CaseNumber = "1" };
        Assert.Null(LitigationBridgeWriter.RecordCaseUpserted(db, legacy, new LitigationBridgeOptions { Enabled = false }, DateTime.UtcNow));
        Assert.Null(LitigationBridgeWriter.RecordCaseUpserted(db, legacy, null, DateTime.UtcNow));
        Assert.Empty(db.ChangeTracker.Entries<ModuleBridgeOutbox>());
    }

    [Fact]
    public async Task Backfill_maps_every_existing_case_and_a_second_run_is_a_no_op()
    {
        var (requestId, ids) = await SeedLegacyAsync(5, withOutbox: false);
        await using (var db = Ctx())
        {
            var first = await LitigationBridgeBackfill.RunAsync(db, On, ids[0] - 1, 5, default);
            Assert.Equal(5, first.Applied);
        }
        var state = await ModuleStateAsync(requestId);
        Assert.Equal("C-1,C-2,C-3,C-4,C-5", state.Aliases);

        await using (var db = Ctx()) await LitigationBridgeBackfill.RunAsync(db, On, ids[0] - 1, 5, default);
        Assert.Equal(state, await ModuleStateAsync(requestId));

    }

    [Fact]
    public async Task Backfill_resumes_from_the_last_id_it_reported()
    {
        var (requestId, ids) = await SeedLegacyAsync(4, withOutbox: false);
        await using var db = Ctx();
        var part1 = await LitigationBridgeBackfill.RunAsync(db, On, ids[0] - 1, 2, default);
        Assert.Equal(ids[1], part1.LastLegacyCaseId);
        Assert.Equal(2, (await ModuleStateAsync(requestId)).Cases);

        await using var db2 = Ctx();
        var part2 = await LitigationBridgeBackfill.RunAsync(db2, On, part1.LastLegacyCaseId, 10, default);
        Assert.Equal(2, part2.Applied);
        Assert.Equal(4, (await ModuleStateAsync(requestId)).Cases);
    }

    [Fact]
    public async Task An_overlay_set_before_the_backfill_runs_again_is_never_lost()
    {
        var (requestId, ids) = await SeedLegacyAsync(2, withOutbox: false);
        await using (var db = Ctx()) await LitigationBridgeBackfill.RunAsync(db, On, ids[0] - 1, 10, default);

        await using (var db = Ctx())
        {
            var store = new LitigationModuleStore(db);
            var scope = await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.Kind == ModuleScopeKind.Request && s.ExternalId == requestId);
            var mc = await store.EnsureCaseForLegacyAsync(ids[0], default);
            Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(IsHidden: true, AnalystNote: "analyst edit"), scope.Version, default));
        }
        await using (var db = Ctx()) await LitigationBridgeBackfill.RunAsync(db, On, ids[0] - 1, 10, default);   // the bridge applies again

        await using var verify = Ctx();
        var link = await (from sc in verify.Set<ModuleScopeCase>().AsNoTracking()
            join mc in verify.Set<ModuleCase>().AsNoTracking() on sc.CaseId equals mc.CaseId
            where mc.LegacyLitigationCaseId == ids[0] select sc).FirstAsync();
        Assert.True(link.IsHidden);
        Assert.Equal("analyst edit", link.AnalystNote);
    }
}
