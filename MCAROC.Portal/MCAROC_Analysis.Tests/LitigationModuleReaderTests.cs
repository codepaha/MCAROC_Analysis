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

    [Fact]
    public async Task Parity_checks_aliases_and_overlays_and_flags_an_overlay_outside_the_retained_set()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Parity Subject", null, default);
        var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, w.RequestId, subject.SubjectId, default);
        var a = await store.AddCaseToScopeAsync(scope.ScopeId, (await store.EnsureCaseForLegacyAsync(w.CaseIds[0], default)).CaseId, null, default);
        Assert.Equal("C-1", a.Alias);

        var clean = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.True(clean.IsClean, string.Join("; ", clean.UnexplainedDifferences));

        // A stored overlay on a case that is not part of the scope's retained set is an inconsistency.
        var strayCase = await store.EnsureCaseForLegacyAsync(long.MaxValue - 7, default);
        await store.AddCaseToScopeAsync(scope.ScopeId, strayCase.CaseId, null, default);
        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.False(report.IsClean);
        Assert.Contains(report.UnexplainedDifferences, d => d.Contains("overlay in this scope but is not in the scope's retained set"));
    }

    [Fact]
    public async Task Parity_compares_the_default_view_with_the_hidden_state_and_explains_hidden_cases()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Parity Subject 2", null, default);
        var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, w.RequestId, subject.SubjectId, default);
        var mc = await store.EnsureCaseForLegacyAsync(w.CaseIds[1], default);
        await store.AddCaseToScopeAsync(scope.ScopeId, mc.CaseId, null, default);
        var v = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == scope.ScopeId)).Version;
        await store.SetOverlayAsync(scope.ScopeId, mc.CaseId, new ScopeOverlayChange(IsHidden: true, IsExcluded: true), v, default);

        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.True(report.IsClean, string.Join("; ", report.UnexplainedDifferences));
        Assert.Contains(report.ExplainedDifferences, d => d.Contains($"case {w.CaseIds[1]} is hidden in this scope"));
    }

    [Fact]
    public void Field_comparison_reports_each_field_that_differs()
    {
        var legacy = new Dictionary<long, LitigationModuleParity.LegacyRow>
        {
            [1] = new(1, "413", "OA", "2022", null, null, "DRT Mumbai 1", "PENDING", null, "01-09-2026", "20-10-2026"),
            [2] = new(2, "88", "OA", "2014", null, null, "DRT Delhi 2", "DISPOSED", null, null, null)
        };
        ModuleCaseSummary Item(long id, string number, string? court, string? status, LitigationCaseStatusBucket bucket, string? last, string? next, bool inFeed = true) =>
            new(id, null, null, number, MCAROC_Analysis.Services.LitigationModule.Normalisation.CaseNumberCompleteness.Complete, court, status, bucket, last, next, inFeed, false, false);
        var reader = new Dictionary<long, ModuleCaseSummary>
        {
            [1] = Item(1, "OA/414/2022", "DRT Mumbai 2", "DISPOSED", LitigationCaseStatusBucket.Disposed, "02-09-2026", "21-10-2026", inFeed: false),
            [2] = Item(2, "OA/88/2014", "DRT Delhi 2", "DISPOSED", LitigationCaseStatusBucket.Disposed, null, null)
        };

        var diffs = LitigationModuleParity.FieldDifferences(legacy, reader);

        foreach (var field in new[] { "case number", "court", "status", "status bucket", "last hearing date", "next hearing date", "in the latest feed" })
            Assert.Contains(diffs, d => d.StartsWith("case 1:") && d.Contains(field));
        Assert.DoesNotContain(diffs, d => d.StartsWith("case 2:"));
    }

    [Fact]
    public void Identical_data_has_no_field_differences()
    {
        var legacy = new Dictionary<long, LitigationModuleParity.LegacyRow> { [1] = new(1, "413", "OA", "2022", null, null, "DRT Mumbai 1", "PENDING", null, null, null) };
        var reader = new Dictionary<long, ModuleCaseSummary>
        {
            [1] = new(1, null, null, "OA/413/2022", MCAROC_Analysis.Services.LitigationModule.Normalisation.CaseNumberCompleteness.Complete, "DRT Mumbai 1", "PENDING",
                LitigationCaseStatusBucket.Pending, null, null, true, false, false)
        };
        Assert.Empty(LitigationModuleParity.FieldDifferences(legacy, reader));
    }

    [Fact]
    public void Overlay_comparison_reports_note_pinned_edits_reason_inclusion_and_redirects()
    {
        var stored = new Dictionary<long, ModuleScopeCase>
        {
            [1] = new() { Alias = "C-1", IsExcluded = true, ExclusionReason = "namesake", AnalystNote = "checked with RM", PinnedFieldsJson = "{\"status\":\"DISPOSED\"}", IncludedInReport = false, RedirectAliasesJson = "[\"C-9\"]" }
        };
        ModuleCaseSummary Shown(string? reason, string? note, string? pinned, bool included, string? redirects) =>
            new(1, 5, "C-1", "OA/1/2022", MCAROC_Analysis.Services.LitigationModule.Normalisation.CaseNumberCompleteness.Complete, null, null, LitigationCaseStatusBucket.Unknown,
                null, null, true, false, true, reason, note, pinned, included, redirects);

        Assert.Empty(LitigationModuleParity.OverlayDifferences(stored,
            new Dictionary<long, ModuleCaseSummary> { [1] = Shown("namesake", "checked with RM", "{\"status\":\"DISPOSED\"}", false, "[\"C-9\"]") }));

        var diffs = LitigationModuleParity.OverlayDifferences(stored, new Dictionary<long, ModuleCaseSummary> { [1] = Shown(null, "other", "{}", true, null) });
        foreach (var field in new[] { "exclusion reason", "analyst note", "pinned field edits", "report inclusion", "redirect aliases" })
            Assert.Contains(diffs, d => d.Contains(field));
    }

    [Fact]
    public async Task Notes_pinned_edits_and_report_inclusion_set_through_the_store_are_read_back_unchanged_and_parity_stays_clean()
    {
        var w = await SeedAsync();
        await using var db = Ctx();
        var store = new LitigationModuleStore(db);
        var subject = await store.CreateSubjectAsync(ModuleSubjectKind.Company, "Parity Subject 3", null, default);
        var scope = await store.EnsureScopeAsync(ModuleScopeKind.Request, w.RequestId, subject.SubjectId, default);
        var mc = await store.EnsureCaseForLegacyAsync(w.CaseIds[0], default);
        await store.AddCaseToScopeAsync(scope.ScopeId, mc.CaseId, null, default);
        var v = (await db.Set<ModuleScope>().AsNoTracking().FirstAsync(s => s.ScopeId == scope.ScopeId)).Version;
        await store.SetOverlayAsync(scope.ScopeId, mc.CaseId,
            new ScopeOverlayChange(IsExcluded: true, ExclusionReason: "namesake", AnalystNote: "not our borrower", IncludedInReport: false, PinnedFieldsJson: "{\"status\":\"DISPOSED\"}"), v, default);

        var item = await new LitigationModuleReader(db).GetCaseAsync(Scope(w), w.CaseIds[0], default);
        Assert.Equal(("namesake", "not our borrower", false, "{\"status\":\"DISPOSED\"}"), (item!.ExclusionReason, item.AnalystNote, item.IncludedInReport, item.PinnedFieldsJson));
        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.True(report.IsClean, string.Join("; ", report.UnexplainedDifferences));
    }
}
