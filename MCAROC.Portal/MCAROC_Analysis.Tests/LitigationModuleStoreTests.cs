using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationModuleStoreTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static long NewExternalId() => Math.Abs(Guid.NewGuid().GetHashCode()) + 1_000_000L;

    private static async Task<(ModuleScope A, ModuleScope B, long[] Cases)> TwoScopesOneSubjectAsync(int caseCount)
    {
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Module Test Subject", null, default);
        var a = await store.EnsureScopeAsync(ModuleScopeKind.Assignment, NewExternalId(), subject.SubjectId, default);
        var b = await store.EnsureScopeAsync(ModuleScopeKind.Assignment, NewExternalId(), subject.SubjectId, default);
        var ids = new List<long>();
        for (var i = 0; i < caseCount; i++)
        {
            var c = await store.EnsureCaseForLegacyAsync(NewExternalId() + i, default);
            ids.Add(c.CaseId);
        }
        return (a, b, ids.ToArray());
    }

    [Fact]
    public async Task Aliases_are_assigned_from_a_per_scope_counter_and_the_same_alias_in_two_scopes_is_two_different_cases()
    {
        var (a, b, cases) = await TwoScopesOneSubjectAsync(2);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var a1 = await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);
        var a2 = await store.AddCaseToScopeAsync(a.ScopeId, cases[1], null, default);
        var b1 = await store.AddCaseToScopeAsync(b.ScopeId, cases[1], null, default);   // the other case first in B

        Assert.Equal(["C-1", "C-2"], [a1.Alias, a2.Alias]);
        Assert.Equal("C-1", b1.Alias);
        Assert.NotEqual(a1.CaseId, b1.CaseId);            // C-1 in A and C-1 in B are different cases
        Assert.Equal(a2.CaseId, b1.CaseId);
    }

    [Fact]
    public async Task A_case_already_in_the_scope_keeps_its_alias()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(1);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var first = await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);
        var again = await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);
        Assert.Equal(first.ScopeCaseId, again.ScopeCaseId);
        Assert.Equal("C-1", again.Alias);
    }

    [Fact]
    public async Task A_legacy_alias_is_imported_verbatim_and_the_counter_moves_past_it()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(2);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var imported = await store.AddCaseToScopeAsync(a.ScopeId, cases[0], "C-12", default);
        var next = await store.AddCaseToScopeAsync(a.ScopeId, cases[1], null, default);
        Assert.Equal("C-12", imported.Alias);
        Assert.Equal("C-13", next.Alias);       // never reuses an alias at or below an imported one
    }

    [Fact]
    public async Task Concurrent_additions_to_one_scope_never_hand_out_the_same_alias()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(12);
        var results = await Task.WhenAll(cases.Select(async id =>
        {
            await using var db = Ctx();
            return (await new LitigationModuleStore(db).AddCaseToScopeAsync(a.ScopeId, id, null, default)).Alias;
        }));
        Assert.Equal(12, results.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 12).Select(i => "C-" + i).OrderBy(x => x), results.OrderBy(x => x));
    }

    [Fact]
    public async Task An_overlay_in_one_scope_does_not_touch_the_other_scope_of_the_same_subject()
    {
        var (a, b, cases) = await TwoScopesOneSubjectAsync(1);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);
        await store.AddCaseToScopeAsync(b.ScopeId, cases[0], null, default);
        var versionA = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version;

        var result = await store.SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(IsHidden: true, AnalystNote: "not ours", IncludedInReport: false), versionA, default);

        Assert.Equal(OverlayWriteResult.Applied, result);
        await using var verify = Ctx();
        var inA = await verify.Set<ModuleScopeCase>().AsNoTracking().FirstAsync(x => x.ScopeId == a.ScopeId);
        var inB = await verify.Set<ModuleScopeCase>().AsNoTracking().FirstAsync(x => x.ScopeId == b.ScopeId);
        Assert.True(inA.IsHidden);
        Assert.Equal("not ours", inA.AnalystNote);
        Assert.False(inA.IncludedInReport);
        Assert.False(inB.IsHidden);
        Assert.Null(inB.AnalystNote);
        Assert.True(inB.IncludedInReport);
        Assert.Equal(versionA + 1, (await verify.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version);
    }

    [Fact]
    public async Task A_stale_scope_version_is_a_conflict_not_an_overwrite()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(1);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);
        var seen = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version;

        Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(AnalystNote: "first"), seen, default));
        Assert.Equal(OverlayWriteResult.VersionConflict, await store.SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(AnalystNote: "second"), seen, default));

        await using var verify = Ctx();
        Assert.Equal("first", (await verify.Set<ModuleScopeCase>().AsNoTracking().FirstAsync(x => x.ScopeId == a.ScopeId)).AnalystNote);
    }

    [Fact]
    public async Task An_overlay_for_a_case_that_is_not_in_the_scope_is_not_found_and_does_not_bump_the_version()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(1);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var seen = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version;
        Assert.Equal(OverlayWriteResult.NotFound, await store.SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(IsHidden: true), seen, default));
        await using var verify = Ctx();
        Assert.Equal(seen, (await verify.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version);
    }

    [Fact]
    public async Task A_legacy_reference_maps_to_one_permanent_case_id()
    {
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var legacy = NewExternalId();
        var first = await store.EnsureCaseForLegacyAsync(legacy, default);
        var again = await store.EnsureCaseForLegacyAsync(legacy, default);
        Assert.Equal(first.CaseId, again.CaseId);

        var job = NewExternalId();
        var row = await store.EnsureCaseForLegacyRowAsync(job, "C-7", default);
        Assert.Equal(row.CaseId, (await store.EnsureCaseForLegacyRowAsync(job, "C-7", default)).CaseId);
        Assert.NotEqual(row.CaseId, (await store.EnsureCaseForLegacyRowAsync(job, "C-8", default)).CaseId);
    }

    [Fact]
    public async Task One_scope_per_kind_and_external_id()
    {
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Individual, "Someone", null, default);
        var ext = NewExternalId();
        var first = await store.EnsureScopeAsync(ModuleScopeKind.Request, ext, subject.SubjectId, default);
        var again = await store.EnsureScopeAsync(ModuleScopeKind.Request, ext, subject.SubjectId, default);
        var other = await store.EnsureScopeAsync(ModuleScopeKind.Assignment, ext, subject.SubjectId, default);   // same id, other kind: another scope
        Assert.Equal(first.ScopeId, again.ScopeId);
        Assert.NotEqual(first.ScopeId, other.ScopeId);
    }

    [Fact]
    public async Task Concurrent_creation_of_the_same_legacy_case_returns_one_row()
    {
        var legacy = NewExternalId();
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Ctx();
            return (await new LitigationModuleStore(db).EnsureCaseForLegacyAsync(legacy, default)).CaseId;
        }));
        Assert.Single(ids.Distinct());

        var job = NewExternalId();
        var rowIds = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = Ctx();
            return (await new LitigationModuleStore(db).EnsureCaseForLegacyRowAsync(job, "C-3", default)).CaseId;
        }));
        Assert.Single(rowIds.Distinct());
    }

    [Fact]
    public async Task An_overlay_applied_through_a_stale_tracked_association_keeps_another_writers_fields()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(1);
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        await store.AddCaseToScopeAsync(a.ScopeId, cases[0], null, default);   // the association is now tracked in db

        await using (var other = Ctx())    // another writer sets the note
        {
            var v = (await other.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version;
            Assert.Equal(OverlayWriteResult.Applied, await new LitigationModuleStore(other).SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(AnalystNote: "from the other writer"), v, default));
        }

        var current = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).Version;
        Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(a.ScopeId, cases[0], new ScopeOverlayChange(IsHidden: true), current, default));

        await using var verify = Ctx();
        var link = await verify.Set<ModuleScopeCase>().AsNoTracking().FirstAsync(x => x.ScopeId == a.ScopeId);
        Assert.True(link.IsHidden);
        Assert.Equal("from the other writer", link.AnalystNote);
    }

    [Fact]
    public async Task Hiding_a_case_in_a_request_scope_bumps_that_requests_live_data_version_but_a_note_does_not()
    {
        await using var seedDb = Ctx();
        var client = new Client { ClientCode = "BW" + Guid.NewGuid().ToString("N")[..8], ClientName = "Bump Test Co", CreatedDate = DateTime.UtcNow };
        seedDb.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Bump Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"BW-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        seedDb.Requests.Add(request);
        await seedDb.SaveChangesAsync();

        var store = new LitigationModuleStore(seedDb);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Bump Test Company", null, default);
        var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, request.RequestId, subject.SubjectId, default);
        var mc = await store.EnsureCaseForLegacyAsync(NewExternalId(), default);
        await store.AddCaseToScopeAsync(scope.ScopeId, mc.CaseId, null, default);
        long Version() => seedDb.Requests.AsNoTracking().Where(r => r.RequestId == request.RequestId).Select(r => r.LiveDataVersion).First();
        long ScopeVersion() => seedDb.Set<ModuleScope>().AsNoTracking().Where(s => s.ScopeId == scope.ScopeId).Select(s => s.Version).First();

        Assert.Equal(0, Version());
        Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(AnalystNote: "just a note"), ScopeVersion(), default));
        Assert.Equal(0, Version());                                                                    // a note changes nothing the pages include
        Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(IsHidden: true), ScopeVersion(), default));
        Assert.Equal(1, Version());
        Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(IsHidden: false), ScopeVersion(), default));
        Assert.Equal(2, Version());                                                                    // un-hiding is a publication too
    }

    [Fact]
    public async Task Forty_concurrent_additions_to_one_scope_get_forty_distinct_consecutive_aliases()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(40);
        var aliases = await Task.WhenAll(cases.Select(async id =>
        {
            await using var db = Ctx();
            return (await new LitigationModuleStore(db).AddCaseToScopeAsync(a.ScopeId, id, null, default)).Alias;
        }));
        Assert.Equal(40, aliases.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 40).Select(i => "C-" + i).OrderBy(x => x), aliases.OrderBy(x => x));
        await using var v = Ctx();
        Assert.Equal(41, (await v.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == a.ScopeId)).NextAliasIndex);   // the counter is exactly one past the last alias
    }

    [Fact]
    public async Task The_same_case_added_by_several_callers_at_once_gets_one_alias_and_one_row()
    {
        var (a, _, cases) = await TwoScopesOneSubjectAsync(1);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var db = Ctx();
            return (await new LitigationModuleStore(db).AddCaseToScopeAsync(a.ScopeId, cases[0], null, default)).Alias;
        }));
        Assert.Single(results.Distinct());
        await using var v = Ctx();
        Assert.Equal(1, await v.Set<ModuleScopeCase>().CountAsync(x => x.ScopeId == a.ScopeId && x.CaseId == cases[0]));
    }
}
