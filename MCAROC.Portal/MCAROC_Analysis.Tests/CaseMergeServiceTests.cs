using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Merging;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseMergeServiceTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private const string Key = "DRT|DRT-MUMBAI-1|OA|413|2022";

    private sealed record World(long RequestId, long A, long B, long SnapshotOld, long SnapshotNew);

    /// <summary>One request with two cases that are the same case twice: equal gate-complete key, verified link and live record on both. Case A has orders X and Y and the old snapshot;
    /// case B has orders Y (identical) and Z and both snapshots. A is the survivor (more orders).</summary>
    private static async Task<World> SeedAsync(string keyForB = Key, bool verifyB = true)
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "MG" + Guid.NewGuid().ToString("N")[..8], ClientName = "Merge Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Merge Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"MG-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, EntityType = "company", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow, Status = LitigationSearchJobStatus.Completed };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        LitigationReportSnapshot Snap(DateTime at) => new()
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = Guid.NewGuid().ToString("N"), ReportFormat = BprReportFormat.Json,
            RawReportBytes = [1], RawReportByteLength = 1, RetrievedUtc = at, Status = LitigationReportSnapshotStatus.Completed, CreatedUtc = at
        };
        var sOld = Snap(DateTime.UtcNow.AddDays(-5));
        var sNew = Snap(DateTime.UtcNow.AddDays(-1));
        db.LitigationReportSnapshots.AddRange(sOld, sNew);
        await db.SaveChangesAsync();

        LitigationCase Case(string court) => new()
        {
            RequestId = request.RequestId, CaseNumber = "413", CaseType = "OA", CaseYear = "2022", Court = court, CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        var a = Case("DRT Mumbai 1");
        var b = Case("DRT Mumbai 1 (vendor spelling)");
        db.LitigationCases.AddRange(a, b);
        await db.SaveChangesAsync();

        LitigationCaseOrder Order(LitigationCase c, string url, string date) => new() { LitigationCaseId = c.LitigationCaseId, PdfUrl = url, OrderDate = date, OrderType = "Order", CreatedUtc = DateTime.UtcNow.AddHours(-1) };
        db.LitigationCaseOrders.AddRange(Order(a, "https://x/1.pdf", "01-01-2025"), Order(a, "https://x/2.pdf", "02-01-2025"),
            Order(b, "https://x/2.pdf", "02-01-2025"), Order(b, "https://x/3.pdf", "03-01-2025"));
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = a.LitigationCaseId, LitigationReportSnapshotId = sOld.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = b.LitigationCaseId, LitigationReportSnapshotId = sOld.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = b.LitigationCaseId, LitigationReportSnapshotId = sNew.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var store = new LitigationModuleStore(db);
        foreach (var (legacy, key, verified) in new[] { (a.LitigationCaseId, Key, true), (b.LitigationCaseId, keyForB, verifyB) })
        {
            var moduleCase = await store.EnsureCaseForLegacyAsync(legacy, default);
            await db.Set<ModuleCase>().Where(m => m.CaseId == moduleCase.CaseId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CaseKey, key));
            await new LiveProjectionStore(db).PublishAsync(new ObservationInput(moduleCase.CaseId, ObservationSource.DrtPortal, ObservationOutcome.Found, true, DateTime.UtcNow,
                new ParsedCaseSnapshot("PENDING", null, "DRT Mumbai 1", "5-12-2022", "15-9-2026", "20-10-2026", null, null, null, 4)), default);
            await new RequestLinkStore(db).UpsertAsync(request.RequestId, moduleCase.CaseId,
                new PartyMatchResult(verified ? PartyMatchOutcome.Verified : PartyMatchOutcome.Rejected, verified ? "exact" : "extra-words", "MERGE TEST COMPANY LIMITED", null), "respondent", false, null, default);
        }
        return new World(request.RequestId, a.LitigationCaseId, b.LitigationCaseId, sOld.LitigationReportSnapshotId, sNew.LitigationReportSnapshotId);
    }

    private static async Task<long> VersionAsync(long requestId) { await using var db = Ctx(); return await RequestLiveData.ReadAsync(db, requestId, default); }

    [Fact]
    public async Task Two_certain_duplicates_are_soft_merged_into_the_better_case_with_a_journal_and_nothing_deleted()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var result = await new CaseMergeService(db).MergeAsync(w.RequestId, w.B, w.A, default);   // given in the "wrong" order on purpose

        Assert.True(result.Performed);
        Assert.Equal((w.A, w.B), (result.SurvivorCaseId, result.LoserCaseId));
        await using var v = Ctx();
        Assert.Equal(w.A, (await v.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == w.B)).MergedIntoCaseId);
        Assert.Null((await v.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == w.A)).MergedIntoCaseId);
        Assert.Equal(2, await v.LitigationCases.CountAsync(c => c.RequestId == w.RequestId));                   // nothing deleted

        // orders: Z moved to the survivor, the identical Y is flagged as collapsed (not moved)
        var survivorOrders = await v.LitigationCaseOrders.AsNoTracking().Where(o => o.LitigationCaseId == w.A).Select(o => o.PdfUrl).ToListAsync();
        Assert.Contains("https://x/3.pdf", survivorOrders);
        var collapsed = await v.LitigationCaseOrders.AsNoTracking().SingleAsync(o => o.LitigationCaseId == w.B);
        Assert.Equal("https://x/2.pdf", collapsed.PdfUrl);
        Assert.NotNull(collapsed.CollapsedIntoOrderId);
        // snapshot links: the survivor now carries the new snapshot's membership too
        Assert.Contains(w.SnapshotNew, await v.LitigationCaseSourceReports.AsNoTracking().Where(s => s.LitigationCaseId == w.A).Select(s => s.LitigationReportSnapshotId).ToListAsync());

        Assert.Equal(result.ChangedValues, await v.Set<ModuleCaseMergeChange>().CountAsync(c => c.MergeId == result.MergeId));
        Assert.True(await VersionAsync(w.RequestId) > 0);   // the merge bumped the request live-data version
    }

    [Fact]
    public async Task A_merged_loser_is_gone_from_the_reader_and_the_legacy_list_and_the_survivor_carries_the_new_snapshot_membership()
    {
        var w = await SeedAsync();
        await using (var db = Ctx()) await new CaseMergeService(db).MergeAsync(w.RequestId, w.A, w.B, default);

        await using var read = Ctx();
        var page = await new LitigationModuleReader(read).GetCasesAsync(new ScopeRef(ModuleScopeKind.Request, w.RequestId), new CaseFilter(), new PageRequest(), default);
        Assert.Equal([w.A], page.Items.Select(i => i.LegacyLitigationCaseId));
        Assert.True(page.Items[0].InLatestFeed);      // via the re-pointed link
        var legacy = await new MCAROC_Analysis.Services.RequestDetails.LitigationCaseListQuery(read).FilterAsync(w.SnapshotNew, null, null, null, null, w.RequestId, default);
        Assert.Equal([w.A], await legacy.Select(c => c.LitigationCaseId).ToListAsync());
    }

    [Theory]
    [InlineData("DRT|DRT-MUMBAI-2|OA|413|2022", true, MergeRefusal.DifferentCaseKey)]
    [InlineData(Key, false, MergeRefusal.NotLiveVerified)]
    public async Task A_merge_that_is_not_certain_is_refused_and_changes_nothing(string keyForB, bool verifyB, MergeRefusal expected)
    {
        var w = await SeedAsync(keyForB, verifyB);
        await using var db = Ctx();
        var before = await VersionAsync(w.RequestId);
        var result = await new CaseMergeService(db).MergeAsync(w.RequestId, w.A, w.B, default);

        Assert.False(result.Performed);
        Assert.Equal(expected, result.Refusal);
        await using var v = Ctx();
        Assert.Empty(await v.LitigationCases.Where(c => c.RequestId == w.RequestId && c.MergedIntoCaseId != null).ToListAsync());
        Assert.Equal(0, await v.Set<ModuleCaseMerge>().CountAsync(m => m.RequestId == w.RequestId));
        Assert.Equal(before, await VersionAsync(w.RequestId));
    }

    [Fact]
    public async Task Two_merges_of_the_same_pair_at_once_perform_one_and_refuse_the_other()
    {
        var w = await SeedAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ => { await using var db = Ctx(); return await new CaseMergeService(db).MergeAsync(w.RequestId, w.A, w.B, default); }));
        Assert.Equal(1, results.Count(r => r.Performed));
        Assert.All(results.Where(r => !r.Performed), r => Assert.Equal(MergeRefusal.AlreadyMerged, r.Refusal));
        await using var v = Ctx();
        Assert.Equal(1, await v.Set<ModuleCaseMerge>().CountAsync(m => m.RequestId == w.RequestId));
    }

    [Fact]
    public async Task Reversal_restores_every_untouched_value_exactly_and_a_second_reversal_does_nothing()
    {
        var w = await SeedAsync();
        await using var snap = Ctx();
        string Fingerprint(AppDbContext d) => string.Join("|", d.LitigationCaseOrders.AsNoTracking().Where(o => o.LitigationCaseId == w.A || o.LitigationCaseId == w.B).OrderBy(o => o.LitigationCaseOrderId)
            .Select(o => $"{o.LitigationCaseOrderId}:{o.LitigationCaseId}:{o.CollapsedIntoOrderId}").AsEnumerable())
            + "#" + string.Join("|", d.LitigationCaseSourceReports.AsNoTracking().Where(s => s.LitigationCaseId == w.A || s.LitigationCaseId == w.B).OrderBy(s => s.LitigationCaseSourceReportId)
            .Select(s => $"{s.LitigationCaseSourceReportId}:{s.LitigationCaseId}").AsEnumerable())
            + "#" + string.Join("|", d.LitigationCases.AsNoTracking().Where(c => c.LitigationCaseId == w.A || c.LitigationCaseId == w.B).OrderBy(c => c.LitigationCaseId).Select(c => c.MergedIntoCaseId).AsEnumerable());
        var before = Fingerprint(snap);

        long mergeId;
        await using (var db = Ctx()) mergeId = (await new CaseMergeService(db).MergeAsync(w.RequestId, w.A, w.B, default)).MergeId!.Value;
        await using (var db = Ctx()) Assert.NotEqual(before, Fingerprint(db));

        await using (var db = Ctx())
        {
            var report = await new CaseMergeService(db).ReverseAsync(mergeId, default);
            Assert.True(report.Reversed);
            Assert.Empty(report.Conflicts);
            Assert.Empty(report.PostMergeRows);
        }
        await using (var db = Ctx()) Assert.Equal(before, Fingerprint(db));        // untouched rows are exactly as before

        await using (var db = Ctx())
        {
            var again = await new CaseMergeService(db).ReverseAsync(mergeId, default);
            Assert.True(again.AlreadyReversed);
            Assert.False(again.Reversed);
        }
        await using (var db = Ctx()) Assert.Equal(before, Fingerprint(db));
    }

    [Fact]
    public async Task Reversal_after_intervening_work_keeps_later_changes_reports_them_and_loses_no_row()
    {
        var w = await SeedAsync();
        long mergeId;
        await using (var db = Ctx()) mergeId = (await new CaseMergeService(db).MergeAsync(w.RequestId, w.A, w.B, default)).MergeId!.Value;

        long movedOrderId, otherCase;
        await using (var db = Ctx())
        {
            // (1) a live publication changes the survivor's court - not a journaled value, must be untouched
            await db.LitigationCases.Where(c => c.LitigationCaseId == w.A).ExecuteUpdateAsync(s => s.SetProperty(c => c.Court, "Published Later"));
            // (2) a new order is downloaded on the survivor after the merge
            db.LitigationCaseOrders.Add(new LitigationCaseOrder { LitigationCaseId = w.A, PdfUrl = "https://x/new.pdf", OrderDate = "09-09-2026", OrderType = "Order", CreatedUtc = DateTime.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync();
            // (3) the re-pointed order Z is changed afterwards (moved elsewhere by someone else): the merge's AfterValue no longer holds
            movedOrderId = (await db.LitigationCaseOrders.AsNoTracking().SingleAsync(o => o.PdfUrl == "https://x/3.pdf" && o.LitigationCaseId == w.A)).LitigationCaseOrderId;
            otherCase = (await db.LitigationCases.AsNoTracking().FirstAsync(c => c.RequestId == w.RequestId && c.LitigationCaseId != w.A && c.LitigationCaseId != w.B
                || c.LitigationCaseId == w.B)).LitigationCaseId;
            await db.LitigationCaseOrders.Where(o => o.LitigationCaseOrderId == movedOrderId).ExecuteUpdateAsync(s => s.SetProperty(o => o.LitigationCaseId, otherCase));
        }
        int ordersBefore;
        await using (var db = Ctx()) ordersBefore = await db.LitigationCaseOrders.CountAsync(o => o.LitigationCaseId == w.A || o.LitigationCaseId == w.B);

        await using (var db = Ctx())
        {
            var report = await new CaseMergeService(db).ReverseAsync(mergeId, default);
            Assert.True(report.Reversed);
            Assert.Contains(report.Conflicts, c => c.Table == CaseMergeService.TOrders && c.RowId == movedOrderId && c.Why.Contains("changed after the merge"));
            Assert.Contains(report.PostMergeRows, p => p.Contains("added after the merge"));
        }

        await using var v = Ctx();
        Assert.Equal("Published Later", (await v.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == w.A)).Court);               // (1) kept
        Assert.Contains("https://x/new.pdf", await v.LitigationCaseOrders.Where(o => o.LitigationCaseId == w.A).Select(o => o.PdfUrl!).ToListAsync());  // (2) kept on the survivor
        Assert.Equal(otherCase, (await v.LitigationCaseOrders.AsNoTracking().SingleAsync(o => o.LitigationCaseOrderId == movedOrderId)).LitigationCaseId);   // (3) not overwritten by a stale value
        Assert.Null((await v.LitigationCases.AsNoTracking().SingleAsync(c => c.LitigationCaseId == w.B)).MergedIntoCaseId);                      // the loser is selectable again
        Assert.Equal(ordersBefore, await v.LitigationCaseOrders.CountAsync(o => o.LitigationCaseId == w.A || o.LitigationCaseId == w.B));          // no row lost
    }
}
