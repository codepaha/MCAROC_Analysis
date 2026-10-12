using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationReaderGateTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    private static readonly LitigationBridgeOptions Bridge = new() { Enabled = true, BatchSize = 100, MaxAttempts = 1, SyncingBacklogThreshold = 1000 };

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record World(long RequestId, long[] CaseIds);

    /// <summary>Two completed snapshots: cases 0 and 1 only in the old one, case 2 in both, case 3 only in the latest. The legacy register lists 2 and 3; the module's
    /// retained set lists all four.</summary>
    private static async Task<World> SeedAsync()
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "GT" + Guid.NewGuid().ToString("N")[..8], ClientName = "Gate Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Gate Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"GT-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
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
        var old = Snap(DateTime.UtcNow.AddDays(-5));
        var latest = Snap(DateTime.UtcNow.AddDays(-1));
        db.LitigationReportSnapshots.AddRange(old, latest);
        await db.SaveChangesAsync();

        LitigationCase Case(string no) => new()
        {
            RequestId = request.RequestId, CaseNumber = no, CaseType = "OA", CaseYear = "2022", Court = "DRT Mumbai 1", CaseStatus = "PENDING",
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        var cases = new[] { Case("1"), Case("2"), Case("3"), Case("4") };
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();
        void Link(LitigationCase c, LitigationReportSnapshot s) =>
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = s.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        Link(cases[0], old); Link(cases[1], old); Link(cases[2], old); Link(cases[2], latest); Link(cases[3], latest);
        await db.SaveChangesAsync();
        return new World(request.RequestId, cases.Select(c => c.LitigationCaseId).ToArray());
    }

    /// <summary>Runs the bridge backfill so every case has its module association, as the live bridge would.</summary>
    private static async Task BridgeAsync(World w)
    {
        await using var db = Ctx();
        var processor = new LitigationBridgeProcessor(db, Bridge);
        foreach (var id in w.CaseIds) await processor.ApplyAsync(id, DateTime.MinValue, default);
    }

    private static LitigationReaderGate Gate(AppDbContext db, LitigationReaderMode mode) =>
        new(db, Options.Create(new LitigationReaderOptions { CaseList = mode }), Options.Create(Bridge));

    private static async Task<long[]> ListedAsync(long requestId, CaseSetSource source)
    {
        await using var db = Ctx();
        var model = await new LitigationCaseListQuery(db, null, source).BuildAsync(requestId, null, null, 1, 100, null, null, null, default);
        return model!.Cases.Select(c => c.LitigationCaseId).OrderBy(x => x).ToArray();
    }

    [Fact]
    public async Task Legacy_mode_lists_only_the_latest_snapshots_cases()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();
        var source = await Gate(db, LitigationReaderMode.Legacy).ResolveCaseListAsync(w.RequestId, default);

        Assert.Equal(CaseSetSource.Legacy, source);
        Assert.Equal(w.CaseIds[2..], await ListedAsync(w.RequestId, source));
    }

    [Fact]
    public async Task Module_mode_lists_the_retained_set_including_cases_the_latest_snapshot_dropped()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();
        var source = await Gate(db, LitigationReaderMode.Module).ResolveCaseListAsync(w.RequestId, default);

        Assert.Equal(CaseSetSource.Module, source);
        Assert.Equal(w.CaseIds.OrderBy(x => x), await ListedAsync(w.RequestId, source));
    }

    [Fact]
    public async Task Module_mode_leaves_out_a_case_the_scope_hid_and_legacy_mode_does_not()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using (var db = Ctx())
        {
            var hidden = await db.Set<ModuleScopeCase>().FirstAsync(sc => sc.Case!.LegacyLitigationCaseId == w.CaseIds[3]);
            hidden.IsHidden = true;
            await db.SaveChangesAsync();
        }

        Assert.Equal(w.CaseIds[..3].OrderBy(x => x), await ListedAsync(w.RequestId, CaseSetSource.Module));
        Assert.Equal(w.CaseIds[2..], await ListedAsync(w.RequestId, CaseSetSource.Legacy));
    }

    [Fact]
    public async Task A_dead_lettered_bridge_event_makes_module_mode_fall_back_to_legacy()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using (var db = Ctx())
        {
            db.Set<ModuleBridgeOutbox>().Add(new ModuleBridgeOutbox
            {
                Kind = BridgeEventKind.CaseUpserted, LegacyLitigationCaseId = w.CaseIds[0], RequestId = w.RequestId, CreatedUtc = DateTime.UtcNow,
                Attempts = 1, LastError = "boom", DeadLetteredUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        await using var verify = Ctx();

        Assert.Equal(CaseSetSource.Legacy, await Gate(verify, LitigationReaderMode.Module).ResolveCaseListAsync(w.RequestId, default));
    }

    [Fact]
    public async Task Shadow_mode_serves_legacy_and_reports_the_difference()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();
        var gate = Gate(db, LitigationReaderMode.Shadow);

        Assert.Equal(CaseSetSource.Legacy, await gate.ResolveCaseListAsync(w.RequestId, default));
        var comparison = await gate.CompareAsync(w.RequestId, default);
        Assert.NotNull(comparison);
        Assert.Empty(comparison!.OnlyInLegacy);
        Assert.Equal(w.CaseIds[..2].OrderBy(x => x), comparison.OnlyInModule);
    }

    [Fact]
    public void Comparison_of_equal_sets_is_identical_and_otherwise_lists_each_side()
    {
        Assert.True(CaseSetComparison.Of([1, 2, 3], [3, 2, 1]).IsIdentical);
        var c = CaseSetComparison.Of([1, 2], [2, 3]);
        Assert.Equal([1L], c.OnlyInLegacy);
        Assert.Equal([3L], c.OnlyInModule);
    }
}
