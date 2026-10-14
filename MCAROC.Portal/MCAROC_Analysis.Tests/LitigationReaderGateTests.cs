using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Bridge;
using MCAROC_Analysis.Services.LitigationModule.Freshness;
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
        new(db, Options.Create(new LitigationReaderOptions { CaseList = mode, CasePage = mode }), Options.Create(Bridge));

    private static LitigationCasePageService Pages(AppDbContext db, LitigationReaderMode mode) => new(db, null, null, Gate(db, mode));

    private static CourtRefreshStatusService StatusService(AppDbContext db, LitigationReaderMode mode) =>
        new(db, null, Gate(db, mode));

    private static LitigationReportAssembler Reports(AppDbContext db, LitigationReaderMode mode) =>
        new(db, StatusService(db, mode), null, Gate(db, mode));

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
    public async Task The_case_page_serves_a_retained_case_only_in_module_mode()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var legacy = Ctx();
        await using var module = Ctx();

        Assert.Null(await Pages(legacy, LitigationReaderMode.Legacy).GetAsync(w.RequestId, w.CaseIds[0], default));   // only an older snapshot lists it
        Assert.NotNull(await Pages(legacy, LitigationReaderMode.Legacy).GetAsync(w.RequestId, w.CaseIds[3], default));
        Assert.NotNull(await Pages(module, LitigationReaderMode.Module).GetAsync(w.RequestId, w.CaseIds[0], default));
    }

    [Fact]
    public async Task The_case_page_of_a_hidden_case_is_not_found_in_module_mode()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using (var db = Ctx())
        {
            var hidden = await db.Set<ModuleScopeCase>().FirstAsync(sc => sc.Case!.LegacyLitigationCaseId == w.CaseIds[3]);
            hidden.IsHidden = true;
            await db.SaveChangesAsync();
        }
        await using var v = Ctx();

        Assert.Null(await Pages(v, LitigationReaderMode.Module).GetAsync(w.RequestId, w.CaseIds[3], default));
        Assert.NotNull(await Pages(v, LitigationReaderMode.Module).GetAsync(w.RequestId, w.CaseIds[2], default));
    }

    [Fact]
    public async Task An_old_link_to_a_merged_duplicate_resolves_to_the_survivor_only_in_module_mode()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using (var db = Ctx())
            await db.LitigationCases.Where(c => c.LitigationCaseId == w.CaseIds[2]).ExecuteUpdateAsync(s => s.SetProperty(c => c.MergedIntoCaseId, w.CaseIds[3]));
        await using var v = Ctx();

        Assert.Equal(w.CaseIds[3], await Pages(v, LitigationReaderMode.Module).GetSurvivorIdAsync(w.RequestId, w.CaseIds[2], default));
        Assert.Null(await Pages(v, LitigationReaderMode.Legacy).GetSurvivorIdAsync(w.RequestId, w.CaseIds[2], default));
        Assert.Null(await Pages(v, LitigationReaderMode.Module).GetSurvivorIdAsync(w.RequestId, w.CaseIds[3], default));   // not merged: nothing to resolve
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, true)]    // exactly the limit still resolves to the terminal survivor
    [InlineData(9, false)]   // beyond it the link is not followed
    public async Task A_merge_chain_resolves_up_to_the_hop_limit(int hops, bool resolves)
    {
        var w = await SeedAsync();
        long[] chain;
        await using (var db = Ctx())
        {
            var extra = Enumerable.Range(0, hops + 1).Select(i => new LitigationCase
            {
                RequestId = w.RequestId, CaseNumber = (900 + i).ToString(), CaseType = "OA", CaseYear = "2022", Court = "DRT Mumbai 1", CaseStatus = "PENDING",
                FirstSeenUtc = DateTime.UtcNow, LastSeenUtc = DateTime.UtcNow
            }).ToList();
            db.LitigationCases.AddRange(extra);
            await db.SaveChangesAsync();
            chain = extra.Select(c => c.LitigationCaseId).ToArray();
            for (var i = 0; i < hops; i++) extra[i].MergedIntoCaseId = chain[i + 1];
            await db.SaveChangesAsync();
        }
        await using var v = Ctx();

        var survivor = await Pages(v, LitigationReaderMode.Module).GetSurvivorIdAsync(w.RequestId, chain[0], default);
        Assert.Equal(resolves ? chain[^1] : null, survivor);
    }
    [Fact]
    public void Comparison_of_equal_sets_is_identical_and_otherwise_lists_each_side()
    {
        Assert.True(CaseSetComparison.Of([1, 2, 3], [3, 2, 1]).IsIdentical);
        var c = CaseSetComparison.Of([1, 2], [2, 3]);
        Assert.Equal([1L], c.OnlyInLegacy);
        Assert.Equal([3L], c.OnlyInModule);
    }

    [Fact]
    public async Task Litigation_report_reads_through_gate_in_legacy_and_module_modes_and_counts_agree()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();

        var legacyReport = await Reports(db, LitigationReaderMode.Legacy).AssembleAsync(w.RequestId, default, includeAnalysis: false);
        Assert.NotNull(legacyReport);
        Assert.Equal(w.CaseIds[2..], legacyReport!.Cases.Select(c => c.LitigationCaseId).OrderBy(x => x));
        Assert.Equal(2, legacyReport.Cases.Count);
        Assert.Equal(2, legacyReport.CourtSummaryGrid.TotalCases);
        Assert.Equal(2, legacyReport.DataCurrency?.TotalCases);

        var legacyListModel = await new LitigationCaseListQuery(db, null, CaseSetSource.Legacy, StatusService(db, LitigationReaderMode.Legacy))
            .BuildAsync(w.RequestId, null, null, 1, 100, null, null, null, default);
        Assert.Equal(legacyReport.Cases.Count, legacyListModel!.Total);
        Assert.Equal(legacyReport.Cases.Count, legacyListModel.NotUpdatedCount);

        var moduleReport = await Reports(db, LitigationReaderMode.Module).AssembleAsync(w.RequestId, default, includeAnalysis: false);
        Assert.NotNull(moduleReport);
        Assert.Equal(w.CaseIds.OrderBy(x => x), moduleReport!.Cases.Select(c => c.LitigationCaseId).OrderBy(x => x));
        Assert.Equal(4, moduleReport.Cases.Count);
        Assert.Equal(4, moduleReport.CourtSummaryGrid.TotalCases);
        Assert.Equal(4, moduleReport.DataCurrency?.TotalCases);

        var moduleListModel = await new LitigationCaseListQuery(db, null, CaseSetSource.Module, StatusService(db, LitigationReaderMode.Module))
            .BuildAsync(w.RequestId, null, null, 1, 100, null, null, null, default);
        Assert.Equal(moduleReport.Cases.Count, moduleListModel!.Total);
        Assert.Equal(moduleReport.Cases.Count, moduleListModel.NotUpdatedCount);
    }

    [Fact]
    public async Task Litigation_report_leaves_out_hidden_case_in_module_mode()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using (var db = Ctx())
        {
            var hidden = await db.Set<ModuleScopeCase>().FirstAsync(sc => sc.Case!.LegacyLitigationCaseId == w.CaseIds[3]);
            hidden.IsHidden = true;
            await db.SaveChangesAsync();
        }

        await using var verify = Ctx();
        var moduleReport = await Reports(verify, LitigationReaderMode.Module).AssembleAsync(w.RequestId, default, includeAnalysis: false);
        Assert.NotNull(moduleReport);
        Assert.Equal(w.CaseIds[..3].OrderBy(x => x), moduleReport!.Cases.Select(c => c.LitigationCaseId).OrderBy(x => x));
        Assert.Equal(3, moduleReport.Cases.Count);
        Assert.Equal(3, moduleReport.CourtSummaryGrid.TotalCases);
        Assert.Equal(3, moduleReport.DataCurrency?.TotalCases);

        var moduleListModel = await new LitigationCaseListQuery(verify, null, CaseSetSource.Module, StatusService(verify, LitigationReaderMode.Module))
            .BuildAsync(w.RequestId, null, null, 1, 100, null, null, null, default);
        Assert.Equal(3, moduleListModel!.Total);
    }

    [Fact]
    public async Task Litigation_tab_and_list_counts_agree_under_both_modes()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();

        var req = await db.Requests.FirstAsync(r => r.RequestId == w.RequestId);

        // Legacy mode
        var vmLegacy = new RequestDetailsViewModel { Request = req, Documents = [] };
        var legacyGate = Gate(db, LitigationReaderMode.Legacy);
        var legacyStatus = StatusService(db, LitigationReaderMode.Legacy);
        await new LitigationDetailsQuery(db, null, null, null, legacyGate, legacyStatus)
            .LoadAsync(vmLegacy, null, null, 1, null, null, default, compact: true);

        Assert.NotNull(vmLegacy.LitigationDataLake);
        var lakeL = vmLegacy.LitigationDataLake!;
        Assert.Equal(2, lakeL.TotalCaseCount);
        Assert.Equal(2, lakeL.CaseList!.Total);
        Assert.Equal(2, lakeL.CourtSummaryGrid.TotalCases);
        Assert.Equal(2, lakeL.SourceCoverage.UniqueCasesCount);
        Assert.Equal(2, lakeL.CaseList.NotUpdatedCount);

        // Module mode
        var vmModule = new RequestDetailsViewModel { Request = req, Documents = [] };
        var moduleGate = Gate(db, LitigationReaderMode.Module);
        var moduleStatus = StatusService(db, LitigationReaderMode.Module);
        await new LitigationDetailsQuery(db, null, null, null, moduleGate, moduleStatus)
            .LoadAsync(vmModule, null, null, 1, null, null, default, compact: true);

        Assert.NotNull(vmModule.LitigationDataLake);
        var lakeM = vmModule.LitigationDataLake!;
        Assert.Equal(4, lakeM.TotalCaseCount);
        Assert.Equal(4, lakeM.CaseList!.Total);
        Assert.Equal(4, lakeM.CourtSummaryGrid.TotalCases);
        Assert.Equal(4, lakeM.SourceCoverage.UniqueCasesCount);
        Assert.Equal(4, lakeM.CaseList.NotUpdatedCount);
    }

    [Fact]
    public async Task Court_refresh_status_and_currency_agree_with_gate()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();

        var legacyStatus = StatusService(db, LitigationReaderMode.Legacy);
        var legacyNotUpdated = await legacyStatus.GetWhatIsNotUpdatedSummaryAsync(w.RequestId, default);
        var legacyCurrency = await legacyStatus.GetDataCurrencySummaryAsync(w.RequestId, default);
        Assert.Equal(2, legacyNotUpdated.TotalCases);
        Assert.Equal(2, legacyCurrency.TotalCases);

        var moduleStatus = StatusService(db, LitigationReaderMode.Module);
        var moduleNotUpdated = await moduleStatus.GetWhatIsNotUpdatedSummaryAsync(w.RequestId, default);
        var moduleCurrency = await moduleStatus.GetDataCurrencySummaryAsync(w.RequestId, default);
        Assert.Equal(4, moduleNotUpdated.TotalCases);
        Assert.Equal(4, moduleCurrency.TotalCases);
    }

    [Fact]
    public async Task Dossier_data_currency_matches_dossier_rows_and_matches_only_gated_cases()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();

        var litRow = new Litigation { RequestId = w.RequestId, IngestionRunId = 1, CaseNumber = "1", Court = "DRT Mumbai 1", CaseType = "OA", Litigants = "Bank vs Co" };
        var dossierLit = new[] { litRow };

        // Legacy mode: case 1 was only in the older snapshot, so legacy does not list it -> cannot be matched
        var legacyStatus = StatusService(db, LitigationReaderMode.Legacy);
        var legacyCurrency = await legacyStatus.GetDossierDataCurrencySummaryAsync(w.RequestId, dossierLit, default);
        Assert.Equal(1, legacyCurrency.TotalCases);
        Assert.Equal(0, legacyCurrency.RefreshedCasesCount);
        Assert.Equal(1, legacyCurrency.UnrefreshedCasesCount);

        // Module mode: case 1 is in the retained set, so it matches the court case
        var moduleStatus = StatusService(db, LitigationReaderMode.Module);
        var moduleCurrency = await moduleStatus.GetDossierDataCurrencySummaryAsync(w.RequestId, dossierLit, default);
        Assert.Equal(1, moduleCurrency.TotalCases);
        Assert.Equal(1, moduleCurrency.UnrefreshedCasesCount); // not yet marked refreshed, but matched
    }

    [Fact]
    public async Task Parity_checker_reports_clean_on_seeded_world()
    {
        var w = await SeedAsync();
        await BridgeAsync(w);
        await using var db = Ctx();

        var report = await new LitigationModuleParity(db).CheckRequestAsync(w.RequestId, default);
        Assert.True(report.IsClean, string.Join("; ", report.UnexplainedDifferences));
        Assert.Equal((2, 4), (report.LegacyCount, report.ReaderCount));
    }
}
