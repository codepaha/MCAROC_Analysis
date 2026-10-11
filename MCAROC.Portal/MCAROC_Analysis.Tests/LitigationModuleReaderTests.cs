using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Reading;
using MCAROC_Analysis.Services.LitigationModule.Store;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationModuleReaderTests : IAsyncLifetime
{
    private static AppDbContext Ctx() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = Ctx();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed record World(long RequestId, long[] CaseIds, long OldSnapshot, long LatestSnapshot);

    /// <summary>A request with two completed vendor snapshots and cases: A and B only in the old one, C in both, D only in the latest.</summary>
    private static async Task<World> SeedAsync(int extraCases = 0)
    {
        await using var db = Ctx();
        var client = new Client { ClientCode = "RD" + Guid.NewGuid().ToString("N")[..8], ClientName = "Reader Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Reader Test Company", Cin = "U45203OR1995PLC003982",
            RequestNumber = $"RD-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new LitigationSearchJob { RequestId = request.RequestId, EntityType = "company", ApplicationCustomerId = "1", KeywordsJson = "[]", CreatedUtc = DateTime.UtcNow, Status = LitigationSearchJobStatus.Completed };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        LitigationReportSnapshot Snap(string hash, DateTime at) => new()
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = hash, ReportFormat = BprReportFormat.Json,
            RawReportBytes = [1], RawReportByteLength = 1, RetrievedUtc = at, Status = LitigationReportSnapshotStatus.Completed, CreatedUtc = at
        };
        var old = Snap(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(-5));
        var latest = Snap(Guid.NewGuid().ToString("N"), DateTime.UtcNow.AddDays(-1));
        db.LitigationReportSnapshots.AddRange(old, latest);
        await db.SaveChangesAsync();

        LitigationCase Case(string no, string status) => new()
        {
            RequestId = request.RequestId, CaseNumber = no, CaseType = "OA", CaseYear = "2022", Court = "DRT Mumbai 1", CaseStatus = status,
            FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
        };
        var cases = new List<LitigationCase> { Case("1", "PENDING"), Case("2", "DISPOSED"), Case("3", "PENDING"), Case("4", "NA") };
        for (var i = 0; i < extraCases; i++) cases.Add(Case((100 + i).ToString(), i % 2 == 0 ? "PENDING" : "DISPOSED"));
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();

        void Link(LitigationCase c, LitigationReportSnapshot s) =>
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = c.LitigationCaseId, LitigationReportSnapshotId = s.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        Link(cases[0], old); Link(cases[1], old); Link(cases[2], old); Link(cases[2], latest); Link(cases[3], latest);
        foreach (var extra in cases.Skip(4)) Link(extra, latest);
        await db.SaveChangesAsync();
        return new World(request.RequestId, cases.Select(c => c.LitigationCaseId).ToArray(), old.LitigationReportSnapshotId, latest.LitigationReportSnapshotId);
    }

    private static ScopeRef Scope(World w) => new(ModuleScopeKind.Request, w.RequestId);

    [Fact]
    public async Task The_retained_set_includes_cases_only_an_older_snapshot_lists_and_flags_them()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var page = await new LitigationModuleReader(db).GetCasesAsync(Scope(w), new CaseFilter(), new PageRequest(), default);

        Assert.Equal(w.CaseIds, page.Items.Select(i => i.LegacyLitigationCaseId));
        Assert.Equal([false, false, true, true], page.Items.Select(i => i.InLatestFeed));
        Assert.Equal(new ModuleCaseCounts(4, 2, 1, 1, 2, 0), page.Counts);
        Assert.Null(page.NextAfterCaseId);
    }

    [Fact]
    public async Task Status_filter_narrows_the_list_but_the_counts_still_show_every_tab()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var page = await new LitigationModuleReader(db).GetCasesAsync(Scope(w), new CaseFilter(Status: LitigationCaseStatusBucket.Pending), new PageRequest(), default);
        Assert.Equal(2, page.Items.Count);
        Assert.Equal((4, 2, 1, 1), (page.Counts.Total, page.Counts.Pending, page.Counts.Disposed, page.Counts.Unknown));
    }

    [Fact]
    public async Task Paging_is_by_keyset_and_visits_every_case_once()
    {
        var w = await SeedAsync(extraCases: 21);   // 25 cases
        await using var db = Ctx();
        var reader = new LitigationModuleReader(db);
        var seen = new List<long>();
        long? after = null;
        var pages = 0;
        do
        {
            var page = await reader.GetCasesAsync(Scope(w), new CaseFilter(), new PageRequest(after, 10), default);
            seen.AddRange(page.Items.Select(i => i.LegacyLitigationCaseId));
            after = page.NextAfterCaseId;
            pages++;
        } while (after is not null);
        Assert.Equal(3, pages);
        Assert.Equal(w.CaseIds, seen);
    }

    [Fact]
    public async Task A_hidden_case_leaves_the_list_and_returns_when_asked_for()
    {
        var w = await SeedAsync();
        long hiddenLegacy = w.CaseIds[1];
        await using (var db = Ctx())
        {
            var store = new LitigationModuleStore(db);
            var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Reader Test Company", null, default);
            var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, w.RequestId, subject.SubjectId, default);
            var mc = await store.EnsureCaseForLegacyAsync(hiddenLegacy, default);
            var link = await store.AddCaseToScopeAsync(scope.ScopeId, mc.CaseId, null, default);
            var v = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == scope.ScopeId)).Version;
            Assert.Equal(OverlayWriteResult.Applied, await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(IsHidden: true), v, default));
            Assert.Equal("C-1", link.Alias);
        }
        await using var read = Ctx();
        var reader = new LitigationModuleReader(read);
        var normal = await reader.GetCasesAsync(Scope(w), new CaseFilter(), new PageRequest(), default);
        Assert.DoesNotContain(normal.Items, i => i.LegacyLitigationCaseId == hiddenLegacy);
        Assert.Equal(3, normal.Counts.Total);
        Assert.Equal(1, normal.Counts.Hidden);

        var all = await reader.GetCasesAsync(Scope(w), new CaseFilter(IncludeHidden: true), new PageRequest(), default);
        var shown = Assert.Single(all.Items, i => i.LegacyLitigationCaseId == hiddenLegacy);
        Assert.True(shown.IsHidden);
        Assert.Equal("C-1", shown.Alias);
    }

    [Fact]
    public async Task A_case_of_another_request_is_never_returned()
    {
        var w1 = await SeedAsync();
        var w2 = await SeedAsync();
        await using var db = Ctx();
        var reader = new LitigationModuleReader(db);
        Assert.Null(await reader.GetCaseAsync(Scope(w1), w2.CaseIds[0], default));
        Assert.Empty(await reader.GetOrdersAsync(Scope(w1), w2.CaseIds[0], default));
        Assert.NotNull(await reader.GetCaseAsync(Scope(w1), w1.CaseIds[0], default));
    }

    [Fact]
    public async Task An_assignment_scope_is_not_served_yet() =>
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await using var db = Ctx();
            await new LitigationModuleReader(db).GetCasesAsync(new ScopeRef(ModuleScopeKind.Assignment, 1), new CaseFilter(), new PageRequest(), default);
        });

    [Fact]
    public async Task Parity_explains_exactly_the_cases_the_latest_feed_dropped()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);

        Assert.True(report.IsClean, string.Join("; ", report.UnexplainedDifferences));
        Assert.Equal((2, 4), (report.LegacyCount, report.ReaderCount));
        Assert.Equal(2, report.ExplainedDifferences.Count);
        Assert.All(report.ExplainedDifferences, d => Assert.Contains("no longer lists", d));
    }

    [Fact]
    public async Task Parity_flags_a_difference_nothing_explains()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        // A row listed by this request's latest snapshot but owned by another request: legacy (membership only) lists it, the reader (this request's cases) does not.
        var stray = db.LitigationCases.First(c => c.LitigationCaseId == w.CaseIds[3]);
        var other = await SeedAsync();
        stray.RequestId = other.RequestId;
        await db.SaveChangesAsync();

        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.False(report.IsClean);
        Assert.Contains(report.UnexplainedDifferences, d => d.Contains($"case {w.CaseIds[3]} is in the legacy list but not in the reader"));
    }
}
