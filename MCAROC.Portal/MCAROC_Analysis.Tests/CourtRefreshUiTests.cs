using System.Text;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Freshness;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Store;
using MCAROC_Analysis.Services.RequestDetails;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using UglyToad.PdfPig;
using Xunit;

namespace MCAROC_Analysis.Tests;

public sealed class CourtRefreshUiTests
{
    private sealed class TestTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(utcNow);
    }

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    [Fact]
    public async Task StatusService_Returns_Idle_When_No_Run_Exists()
    {
        await using var db = CreateContext();
        var service = new CourtRefreshStatusService(db);

        var status = await service.GetRefreshStatusAsync(1001);

        Assert.Equal("Idle", status.State);
        Assert.Equal(0, status.Percent);
        Assert.Equal(0, status.Handled);
        Assert.Equal(0, status.Total);
        Assert.False(status.Finished);
    }

    [Fact]
    public async Task StatusService_Calculates_Percentage_And_Heartbeat_Pause()
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var timeProvider = new TestTimeProvider(now);

        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var run = new ModuleRefreshRun
        {
            RequestId = req.RequestId,
            Phase = (int)RefreshPhase.RefreshingCases,
            StartedUtc = now.AddMinutes(-30),
            EligibleAtStart = 100,
            LastAgentHeartbeatUtc = now.AddMinutes(-15) // > 10 min old => Paused
        };
        db.Set<ModuleRefreshRun>().Add(run);
        await db.SaveChangesAsync();

        // Add 45 handled work items
        var items = new List<ModuleFetchWorkItem>();
        for (int i = 1; i <= 45; i++)
        {
            var item = new ModuleFetchWorkItem
            {
                Host = (int)FetchHost.Drt,
                Kind = (int)FetchWorkKind.StatusFetch,
                Status = (int)FetchWorkStatus.Succeeded,
                CompletedUtc = now.AddMinutes(-20 + (i % 15))
            };
            items.Add(item);
        }
        db.Set<ModuleFetchWorkItem>().AddRange(items);
        await db.SaveChangesAsync();

        foreach (var item in items)
        {
            db.Set<ModuleFetchWorkItemRun>().Add(new ModuleFetchWorkItemRun { WorkItemId = item.WorkItemId, RunId = run.RunId });
        }
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db, timeProvider);
        var status = await service.GetRefreshStatusAsync(req.RequestId);

        Assert.Equal("Paused", status.State);
        Assert.Equal(45, status.Percent);
        Assert.Equal(45, status.Handled);
        Assert.Equal(100, status.Total);
        Assert.Contains("Paused", status.Message!);
        Assert.Equal("Refreshing cases", status.PhaseLabel);
    }

    [Fact]
    public async Task StatusService_Computes_WhatIsNotUpdated_Summary_Accurately()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");
        var c1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "101/2022",
            CaseType = "WP",
            CaseYear = "2022",
            CaseStatus = "Pending",
            Court = "Delhi High Court",
            State = "Delhi",
            District = "New Delhi",
            FilingDate = "2022-01-01",
            LastHearingDate = "2026-01-01",
            NextHearingDate = futureDate
        };
        var c2 = new LitigationCase { RequestId = reqId, CaseStatus = "Disposed", Court = "HC Bombay", FilingDate = "2020-01-01" };
        var c3 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", Court = null, FilingDate = "2022-05-10" };
        db.LitigationCases.AddRange(c1, c2, c3);
        await db.SaveChangesAsync();

        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "K1" };
        var mc3 = new ModuleCase { LegacyLitigationCaseId = c3.LitigationCaseId, CaseKey = "K3" };
        db.Set<ModuleCase>().AddRange(mc1, mc3);
        await db.SaveChangesAsync();

        var live1 = new ModuleCaseLive { CaseId = mc1.CaseId, LastRetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseLive>().Add(live1);

        var obs1 = new ModuleCaseObservation { CaseId = mc1.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = DateTime.UtcNow };
        var obs3 = new ModuleCaseObservation { CaseId = mc3.CaseId, Outcome = (int)ObservationOutcome.Error, Verified = false, RetrievedUtc = DateTime.UtcNow, AppliedAs = "TransientFailure:500 Internal Error" };
        db.Set<ModuleCaseObservation>().AddRange(obs1, obs3);

        var work3 = new ModuleFetchWorkItem { Host = (int)FetchHost.Drt, CaseId = mc3.CaseId, Kind = (int)FetchWorkKind.StatusFetch, Status = (int)FetchWorkStatus.Failed, LastError = "500 Internal Error" };
        db.Set<ModuleFetchWorkItem>().Add(work3);

        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var summary = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);

        Assert.Equal(3, summary.TotalCases);
        Assert.Equal(1, summary.UpdatedCount);
        Assert.Equal(2, summary.NotUpdatedCount);
        Assert.Equal(1, summary.DisposedCount);
        Assert.Equal(1, summary.FailedAttemptsCount);
        Assert.True(summary.MissingFieldsCount["court"] >= 1);
    }

    [Fact]
    public async Task StatusService_DataCurrencySummary_Matches_Spec()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");
        var c1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "201/2022",
            CaseType = "WP",
            CaseYear = "2022",
            CaseStatus = "Pending",
            Court = "Delhi High Court",
            State = "Delhi",
            District = "New Delhi",
            FilingDate = "2022-01-01",
            LastHearingDate = "2026-01-01",
            NextHearingDate = futureDate
        };
        var c2 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending" };
        db.LitigationCases.AddRange(c1, c2);
        await db.SaveChangesAsync();

        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "K10" };
        db.Set<ModuleCase>().Add(mc1);
        await db.SaveChangesAsync();

        var live1 = new ModuleCaseLive { CaseId = mc1.CaseId, LastRetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseLive>().Add(live1);

        var obs1 = new ModuleCaseObservation { CaseId = mc1.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseObservation>().Add(obs1);

        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var currency = await service.GetDataCurrencySummaryAsync(reqId);

        Assert.Equal(2, currency.TotalCases);
        Assert.Equal(1, currency.RefreshedCasesCount);
        Assert.Equal(1, currency.UnrefreshedCasesCount);
        Assert.Equal("Unrefreshed cases show the last known values.", currency.CurrencyNote);
        Assert.Contains("IST", currency.AsOfIst);
    }

    [Fact]
    public async Task Controller_CourtRefreshStatusEndpoint_Returns_Json_Without_Page_Reload()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var controller = new RequestsController(
            db: db,
            orchestrator: null!,
            fileValidation: null!,
            analysisQueue: null!,
            filingQueue: null!,
            requestListQueryService: null!,
            dossierCache: null!,
            env: null!,
            corporateTimelineBuilder: null!,
            courtRefreshStatus: service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.CourtUpdateStatus(req.RequestId, default);

        var jsonResult = Assert.IsType<JsonResult>(result);
        var dto = Assert.IsType<CourtRefreshStatusDto>(jsonResult.Value);
        Assert.Equal("Idle", dto.State);

        var notFoundResult = await controller.CourtUpdateStatus(req.RequestId + 999999, default);
        Assert.IsType<NotFoundResult>(notFoundResult);
    }

    [Fact]
    public async Task LitigationCaseListQuery_Filters_By_Freshness_NotUpdated()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var job = new LitigationSearchJob { RequestId = reqId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h1" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snap = new LitigationReportSnapshot { RequestId = reqId, LitigationSearchJobId = job.LitigationSearchJobId, ReportHash = "h1", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snap);
        await db.SaveChangesAsync();

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");
        var c1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "301/2022",
            CaseType = "WP",
            CaseYear = "2022",
            Court = "Delhi High Court",
            State = "Delhi",
            District = "New Delhi",
            FilingDate = "2022-01-01",
            LastHearingDate = "2026-01-01",
            NextHearingDate = futureDate,
            CaseStatus = "Pending"
        };
        var c2 = new LitigationCase { RequestId = reqId, CaseNumber = "NUM-2", Court = "Del HC" };
        db.LitigationCases.AddRange(c1, c2);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationReportSnapshotId = snap.LitigationReportSnapshotId, LitigationCaseId = c1.LitigationCaseId });
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationReportSnapshotId = snap.LitigationReportSnapshotId, LitigationCaseId = c2.LitigationCaseId });

        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "K101" };
        db.Set<ModuleCase>().Add(mc1);
        await db.SaveChangesAsync();

        var live1 = new ModuleCaseLive { CaseId = mc1.CaseId, LastRetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseLive>().Add(live1);

        var obs1 = new ModuleCaseObservation { CaseId = mc1.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseObservation>().Add(obs1);

        await db.SaveChangesAsync();

        var statusService = new CourtRefreshStatusService(db);
        var query = new LitigationCaseListQuery(db, null, statusService);

        var listAll = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default);
        Assert.NotNull(listAll);
        Assert.Equal(2, listAll.Cases.Count);

        var listNotUpdated = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default, freshness: "not-updated");
        Assert.NotNull(listNotUpdated);
        Assert.Single(listNotUpdated.Cases);
        Assert.Equal(c2.LitigationCaseId, listNotUpdated.Cases[0].LitigationCaseId);
    }

    [Fact]
    public void StandaloneReport_Renders_DataCurrency_In_Pdf_And_Csv()
    {
        var now = DateTime.UtcNow;
        var currency = new LitigationDataCurrencySummary(
            now,
            Ist.Format(now, "dd-MMM-yyyy, HH:mm"),
            TotalCases: 5,
            RefreshedCasesCount: 3,
            UnrefreshedCasesCount: 2,
            CurrencyNote: "Unrefreshed cases show the last known values.");

        var c1 = new StandaloneReportCaseDto(
            1, "P1", null, "CNR-1", "High Court", "Against", "Commercial", "Writ", "High Court", "Principal",
            "123/2020", "WP", "2020", "Hearing", "Pending", "Act", "2020-01-01", "2026-09-01", null, null,
            "State", "District", "[]", "[]", "[]", "[]", []);

        var report = new StandaloneLitigationReport(
            "REQ-1", "Test Company Ltd", new DateTimeOffset(now), 1, now, false, ["Keyword"],
            new LitigationCourtSummaryGrid(), [c1],
            DataCurrency: currency);

        // PDF check
        var pdfBytes = LitigationReportArtifacts.RenderPdf(report);
        Assert.True(pdfBytes.Length > 500);

        if (OperatingSystem.IsWindows())
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdfBytes));
            var text = string.Concat(doc.GetPages().Select(p => p.Text));
            Assert.Contains("DATA CURRENCY", text);
            Assert.Contains("3 of 5 case(s) refreshed", text);
            Assert.Contains("2 not updated", text);
            Assert.Contains("Unrefreshed cases show the last known values.", text);
        }

        // CSV check
        var csvBytes = LitigationReportArtifacts.RenderCsv(report);
        var csvText = Encoding.UTF8.GetString(csvBytes);
        Assert.Contains("Data Currency As Of", csvText);
        Assert.Contains("Refreshed Cases Count", csvText);
        Assert.Contains("Unrefreshed Cases Count", csvText);
        Assert.Contains("3", csvText);
        Assert.Contains("2", csvText);
        Assert.Contains("Unrefreshed cases show the last known values.", csvText);
    }

    [Fact]
    public void DossierPdfComposer_Renders_DataCurrency_In_AnnexureE()
    {
        var now = DateTime.UtcNow;
        var currency = new LitigationDataCurrencySummary(
            now,
            Ist.Format(now, "dd-MMM-yyyy, HH:mm"),
            TotalCases: 2,
            RefreshedCasesCount: 1,
            UnrefreshedCasesCount: 1,
            CurrencyNote: "Unrefreshed cases show the last known values.");

        var lit = new Litigation { LitigationId = 1, RequestId = 1, CaseStatus = "Pending", Court = "Delhi High Court" };
        var dossierLit = new DossierLitigation([lit], [new LitigationThread("T1", [lit], false)], new Dictionary<long, LitigationRole> { [1] = LitigationRole.FiledAgainst }, DataCurrency: currency);

        var model = new DossierModel(
            RequestId: 1,
            IngestionRunId: 1,
            AnalysisRunId: 1,
            Cover: new DossierCover("Test Corp", "CIN123", "PAN123", null, "Active", "Client", now, now),
            Corporate: new DossierCorporate([], [], [], [], [], [], [], null, null, [], []),
            Financials: new DossierFinancials([], [], [], [], [], []),
            Charges: new DossierCharges([], [], [], [], 0),
            Compliance: new DossierCompliance([], [], [], [], [], [], []),
            Litigation: dossierLit,
            ExecSummary: new DossierExecSummary(null, 0, 0, 0, 0, [], null, []),
            SourceSheets: [],
            SourceCoverage: SheetCoverage.Empty,
            Metrics: [],
            IncludeLitigation: true);

        QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;
        var composer = new DossierPdfComposer(model, DossierVariant.Executive);
        var pdfBytes = composer.GeneratePdf();

        Assert.True(pdfBytes.Length > 1000);

        if (OperatingSystem.IsWindows())
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdfBytes));
            var text = string.Concat(doc.GetPages().Select(p => p.Text));
            Assert.Contains("DATA CURRENCY", text);
            Assert.Contains("1 of 2 case(s) refreshed", text);
            Assert.Contains("1 not updated", text);
            Assert.Contains("Unrefreshed cases show the last known values.", text);
        }
    }

    [Fact]
    public async Task StatusService_Parses_ConfirmedEmpty_Marks_From_FieldSourcesJson()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        // c1 has no NextHearingDate, but FieldSourcesJson marks it ConfirmedEmpty
        var c1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "501/2022",
            CaseType = "WP",
            CaseYear = "2022",
            CaseStatus = "Pending",
            Court = "Delhi High Court",
            State = "Delhi",
            District = "New Delhi",
            FilingDate = "2022-01-01",
            LastHearingDate = "2026-01-01",
            NextHearingDate = null
        };
        db.LitigationCases.Add(c1);
        await db.SaveChangesAsync();

        var sources = new Dictionary<string, FieldSource>
        {
            [LiveProjectionEngine.FNextHearing] = new FieldSource("Portal", DateTime.UtcNow, 10, ConfirmedEmpty: true)
        };
        var sourcesJson = LiveProjectionEngine.SerializeSources(sources);

        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "K501" };
        db.Set<ModuleCase>().Add(mc1);
        await db.SaveChangesAsync();

        var live1 = new ModuleCaseLive { CaseId = mc1.CaseId, LastRetrievedUtc = DateTime.UtcNow, FieldSourcesJson = sourcesJson };
        db.Set<ModuleCaseLive>().Add(live1);

        var obs1 = new ModuleCaseObservation { CaseId = mc1.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = DateTime.UtcNow };
        db.Set<ModuleCaseObservation>().Add(obs1);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var freshness = await service.GetCaseFreshnessAsync(reqId, c1.LitigationCaseId);

        // Within confirmed-empty window, NextHearing is treated as known empty, not missing, so it is Updated!
        Assert.Equal(FreshnessLabel.Updated, freshness.Label);
        Assert.NotNull(freshness.AsOfUtc);
    }

    [Fact]
    public async Task StatusService_Distinguishes_Queued_And_InProgress_Fetch_Items()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var cQueued = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "Q1", Court = "HC Delhi" };
        var cClaimed = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "C1", Court = "HC Bombay" };
        var cEligibleOnly = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "E1", Court = "HC Madras" };
        db.LitigationCases.AddRange(cQueued, cClaimed, cEligibleOnly);
        await db.SaveChangesAsync();

        var mcQ = new ModuleCase { LegacyLitigationCaseId = cQueued.LitigationCaseId, CaseKey = "KQ" };
        var mcC = new ModuleCase { LegacyLitigationCaseId = cClaimed.LitigationCaseId, CaseKey = "KC" };
        var mcE = new ModuleCase { LegacyLitigationCaseId = cEligibleOnly.LitigationCaseId, CaseKey = "KE" };
        db.Set<ModuleCase>().AddRange(mcQ, mcC, mcE);
        await db.SaveChangesAsync();

        // One item Queued, one Claimed (in progress)
        var itemQ = new ModuleFetchWorkItem { Host = (int)FetchHost.Drt, CaseId = mcQ.CaseId, Kind = (int)FetchWorkKind.StatusFetch, Status = (int)FetchWorkStatus.Queued };
        var itemC = new ModuleFetchWorkItem { Host = (int)FetchHost.Drt, CaseId = mcC.CaseId, Kind = (int)FetchWorkKind.StatusFetch, Status = (int)FetchWorkStatus.Claimed };
        db.Set<ModuleFetchWorkItem>().AddRange(itemQ, itemC);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var summary = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);

        Assert.Equal(3, summary.TotalCases);
        Assert.Equal(1, summary.QueuedCount);
        Assert.Equal(1, summary.InProgressCount); // Only the claimed item, NOT the eligible-only one
        Assert.Equal(3, summary.NotUpdatedCount);
    }

    [Fact]
    public async Task LitigationCaseListQuery_Matches_CaseValue_Placeholders()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var job = new LitigationSearchJob { RequestId = reqId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "h-pl" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snap = new LitigationReportSnapshot { RequestId = reqId, LitigationSearchJobId = job.LitigationSearchJobId, ReportHash = "h-pl", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow };
        db.LitigationReportSnapshots.Add(snap);
        await db.SaveChangesAsync();

        var c1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "P-1",
            Court = "NIL",
            CaseStatus = "DUMMY",
            FilingDate = "0000-00-00",
            State = "NA"
        };
        db.LitigationCases.Add(c1);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationReportSnapshotId = snap.LitigationReportSnapshotId, LitigationCaseId = c1.LitigationCaseId });
        await db.SaveChangesAsync();

        var query = new LitigationCaseListQuery(db);

        var listStatus = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default, missingField: "status");
        Assert.NotNull(listStatus);
        Assert.Single(listStatus.Cases);

        var listCourt = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default, missingField: "court");
        Assert.NotNull(listCourt);
        Assert.Single(listCourt.Cases);

        var listFiling = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default, missingField: "filing-date");
        Assert.NotNull(listFiling);
        Assert.Single(listFiling.Cases);

        var listState = await query.BuildAsync(reqId, null, null, 1, 25, null, null, null, default, missingField: "state");
        Assert.NotNull(listState);
        Assert.Single(listState.Cases);
    }

    [Fact]
    public async Task StatusService_Retains_Specific_Failure_Reasons_In_Freshness_And_CommonFailureReasons()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var c1 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "F-1", Court = "DRT Mumbai" };
        var c2 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "F-2", Court = "DRT Delhi" };
        var c3 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "F-3", Court = "DRT Chennai" };
        db.LitigationCases.AddRange(c1, c2, c3);
        await db.SaveChangesAsync();

        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "KF1" };
        var mc2 = new ModuleCase { LegacyLitigationCaseId = c2.LitigationCaseId, CaseKey = "KF2" };
        var mc3 = new ModuleCase { LegacyLitigationCaseId = c3.LitigationCaseId, CaseKey = "KF3" };
        db.Set<ModuleCase>().AddRange(mc1, mc2, mc3);
        await db.SaveChangesAsync();

        var now = DateTime.UtcNow;
        db.Set<ModuleCaseObservation>().AddRange(
            new ModuleCaseObservation { CaseId = mc1.CaseId, Outcome = (int)ObservationOutcome.Error, Verified = false, RetrievedUtc = now },
            new ModuleCaseObservation { CaseId = mc2.CaseId, Outcome = (int)ObservationOutcome.Error, Verified = false, RetrievedUtc = now },
            new ModuleCaseObservation { CaseId = mc3.CaseId, Outcome = (int)ObservationOutcome.Error, Verified = false, RetrievedUtc = now, AppliedAs = "Gateway timeout" });

        db.Set<ModuleFetchWorkItem>().AddRange(
            new ModuleFetchWorkItem { Host = (int)FetchHost.Drt, CaseId = mc1.CaseId, Kind = (int)FetchWorkKind.StatusFetch, Status = (int)FetchWorkStatus.Failed, CompletedUtc = now, LastError = "HTTP 502 Bad Gateway" },
            new ModuleFetchWorkItem { Host = (int)FetchHost.Drt, CaseId = mc2.CaseId, Kind = (int)FetchWorkKind.StatusFetch, Status = (int)FetchWorkStatus.Failed, CompletedUtc = now, LastError = "HTTP 502 Bad Gateway" });
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var f1 = await service.GetCaseFreshnessAsync(reqId, c1.LitigationCaseId);
        var f3 = await service.GetCaseFreshnessAsync(reqId, c3.LitigationCaseId);

        Assert.Equal(FreshnessLabel.CouldNotVerify, f1.Label);
        Assert.Equal("HTTP 502 Bad Gateway", f1.Reason);
        Assert.Equal(FreshnessLabel.CouldNotVerify, f3.Label);
        Assert.Equal("Gateway timeout", f3.Reason);

        var summary = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);
        Assert.Equal(3, summary.FailedAttemptsCount);
        Assert.Contains(summary.CommonFailureReasons, r => r.Reason == "HTTP 502 Bad Gateway" && r.Count == 2);
        Assert.Contains(summary.CommonFailureReasons, r => r.Reason == "Gateway timeout" && r.Count == 1);
    }

    [Fact]
    public async Task StatusService_Computes_PortfolioSummaryPending_From_Portfolio_And_CaseSetHash()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var baseTime = DateTime.UtcNow.AddMinutes(-10);
        var c1 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "PA-1", LastSeenUtc = baseTime };
        db.LitigationCases.Add(c1);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);

        // 1. No portfolio summary yet -> Pending (true)
        var summaryNoPortfolio = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);
        Assert.True(summaryNoPortfolio.PortfolioSummaryPending);

        // 2. Completed portfolio summary matching current CaseSetHash -> Current (false)
        var run = new LitigationAiAnalysisRun
        {
            RequestId = reqId,
            RunNumber = 1,
            Status = LitigationAiAnalysisRunStatus.Completed,
            ModelId = "test-model",
            PromptVersion = "v1",
            CreatedUtc = baseTime,
            CompletedUtc = baseTime.AddMinutes(1)
        };
        db.LitigationAiAnalysisRuns.Add(run);
        await db.SaveChangesAsync();

        var setHash = LitigationAiAnalysisOrchestrator.CaseSetHash([c1.LitigationCaseId]);
        var portfolio = new LitigationPortfolioAiAnalysis
        {
            LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId,
            Status = LitigationAiAnalysisItemStatus.Completed,
            CaseSetHash = setHash,
            EvidenceJson = "{}",
            EvidenceHash = "eh",
            PromptHash = "ph",
            CompletedUtc = baseTime.AddMinutes(1)
        };
        db.LitigationPortfolioAiAnalyses.Add(portfolio);
        await db.SaveChangesAsync();

        var summaryCurrent = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);
        Assert.False(summaryCurrent.PortfolioSummaryPending);

        // 3. Adding a new case changes CaseSetHash -> Pending (true)
        var c2 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "PA-2", LastSeenUtc = baseTime };
        db.LitigationCases.Add(c2);
        await db.SaveChangesAsync();

        var summaryAfterNewCase = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);
        Assert.True(summaryAfterNewCase.PortfolioSummaryPending);
    }

    [Fact]
    public void LitigationTab_CompletionScript_Updates_NotUpdated_Counts_And_Checks_Unverified_Count()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var viewPath = Path.Combine(repoRoot, "MCAROC_Analysis", "Views", "Requests", "Details", "_LitigationTab.cshtml");
        var content = File.ReadAllText(viewPath);

        Assert.Contains("updateNotUpdatedPanel(data.notUpdated)", content);
        Assert.Contains("case(s) stayed unverified", content);
        Assert.DoesNotContain("Court records verified up to date", content);

        // Verify Failed and Paused state transitions update the active banner indicator, progress bar, badge, and alert
        Assert.Contains("var container = document.getElementById('court-refresh-banner-container');", content);
        Assert.Contains("document.getElementById('court-refresh-state-indicator')", content);
        Assert.Contains("stateIndicator.innerHTML = '<i class=\"bi bi-exclamation-triangle-fill text-danger fs-5\"></i><strong class=\"text-danger\">Court update stopped:</strong>'", content);
        Assert.Contains("stateIndicator.innerHTML = '<i class=\"bi bi-pause-circle-fill text-warning fs-5\"></i><strong class=\"text-warning-emphasis\">Court update paused:</strong>'", content);
        Assert.Contains("pBar.className = 'progress-bar progress-bar-striped bg-danger'", content);
        Assert.Contains("pBadge.className = 'badge bg-danger fs-6'", content);
    }

    [Fact]
    public async Task StatusService_OrdersUnsupportedFormatCount_Excludes_Transient_Failed_Downloads()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var c1 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "ORD-1", Court = "DRT Mumbai" };
        db.LitigationCases.Add(c1);
        await db.SaveChangesAsync();

        var oTransientFail = new LitigationCaseOrder { LitigationCaseId = c1.LitigationCaseId, OrderDate = "2026-01-10", OrderType = "Daily Order" };
        var oWordDoc = new LitigationCaseOrder { LitigationCaseId = c1.LitigationCaseId, OrderDate = "2026-01-11", OrderType = "Daily Order" };
        var oPendingPdf = new LitigationCaseOrder { LitigationCaseId = c1.LitigationCaseId, OrderDate = "2026-01-12", OrderType = "Daily Order" };
        db.LitigationCaseOrders.AddRange(oTransientFail, oWordDoc, oPendingPdf);
        await db.SaveChangesAsync();

        // 1. Transient failed download (HTTP 502) -> NOT an unsupported format
        var docFail = new LitigationOrderDocument
        {
            LitigationCaseOrderId = oTransientFail.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Failed,
            FailureReason = "HTTP 502 Bad Gateway",
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5)
        };
        // 2. Downloaded Word document -> IS an unsupported format (and excluded from extraction pending)
        var docWord = new LitigationOrderDocument
        {
            LitigationCaseOrderId = oWordDoc.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            StoragePath = "order-word.docx",
            FailureReason = "Word .docx format is not supported for text extraction in v1",
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5)
        };
        // 3. Downloaded PDF awaiting extraction -> IS extraction pending, NOT unsupported format
        var docPdf = new LitigationOrderDocument
        {
            LitigationCaseOrderId = oPendingPdf.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            ContentType = "application/pdf",
            StoragePath = "order.pdf",
            TextExtractionStatus = null,
            RetainedUntilUtc = DateTime.UtcNow.AddDays(5)
        };
        db.LitigationOrderDocuments.AddRange(docFail, docWord, docPdf);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db);
        var summary = await service.GetWhatIsNotUpdatedSummaryAsync(reqId);

        Assert.Equal(1, summary.OrdersNotDownloadedCount); // oTransientFail
        Assert.Equal(1, summary.OrdersUnsupportedFormatCount); // docWord only, NOT docFail
        Assert.Equal(1, summary.OrdersExtractionPendingCount); // docPdf only, NOT docWord
    }

    [Fact]
    public async Task StatusService_DataCurrencySummary_Uses_Snapshot_Or_CompletedObservation_Not_Now_Or_UnfinishedRunStart()
    {
        await using var db = CreateContext();
        var now = new DateTime(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
        var snapshotTime = new DateTime(2026, 10, 8, 6, 30, 0, DateTimeKind.Utc); // 5 days ago
        var runStartTime = new DateTime(2026, 10, 13, 9, 45, 0, DateTimeKind.Utc); // 15 min ago
        var obsCompletedTime = new DateTime(2026, 10, 13, 9, 55, 0, DateTimeKind.Utc); // 5 min ago

        var timeProvider = new TestTimeProvider(now);
        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}", RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var job = new LitigationSearchJob { RequestId = reqId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "hash-snap" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snap = new LitigationReportSnapshot
        {
            RequestId = reqId,
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash-snap",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = snapshotTime
        };
        db.LitigationReportSnapshots.Add(snap);
        var c1 = new LitigationCase { RequestId = reqId, CaseStatus = "Pending", CaseNumber = "DC-1", Court = "DRT Mumbai" };
        db.LitigationCases.Add(c1);
        await db.SaveChangesAsync();

        var service = new CourtRefreshStatusService(db, timeProvider);

        // 1. No refresh run exists -> AsOfUtc is the authoritative snapshot time (5 days ago), NOT NowUtc
        var currencyBeforeRefresh = await service.GetDataCurrencySummaryAsync(reqId);
        Assert.Equal(snapshotTime, currencyBeforeRefresh.AsOfUtc);
        Assert.Equal(Ist.Format(snapshotTime, "dd-MMM-yyyy, HH:mm"), currencyBeforeRefresh.AsOfIst);
        Assert.False(currencyBeforeRefresh.RefreshInProgress);
        Assert.Null(currencyBeforeRefresh.InProgressNote);

        // 2. Unfinished refresh run in progress -> AsOfUtc stays at snapshotTime (NOT runStartTime or NowUtc), and in-progress refresh is labeled separately
        var run = new ModuleRefreshRun
        {
            RequestId = reqId,
            Phase = (int)RefreshPhase.RefreshingCases,
            StartedUtc = runStartTime,
            EligibleAtStart = 10,
            FinishedUtc = null
        };
        db.Set<ModuleRefreshRun>().Add(run);
        await db.SaveChangesAsync();

        var currencyDuringRun = await service.GetDataCurrencySummaryAsync(reqId);
        Assert.Equal(snapshotTime, currencyDuringRun.AsOfUtc);
        Assert.NotEqual(runStartTime, currencyDuringRun.AsOfUtc);
        Assert.NotEqual(now, currencyDuringRun.AsOfUtc);
        Assert.True(currencyDuringRun.RefreshInProgress);
        Assert.NotNull(currencyDuringRun.InProgressNote);
        Assert.Contains("Court refresh in progress", currencyDuringRun.InProgressNote);
        Assert.Contains(Ist.Format(runStartTime, "dd-MMM-yyyy, HH:mm"), currencyDuringRun.InProgressNote);

        // 3. Completed observation arrives -> AsOfUtc advances to latest completed observation time
        var mc1 = new ModuleCase { LegacyLitigationCaseId = c1.LitigationCaseId, CaseKey = "K-DC1" };
        db.Set<ModuleCase>().Add(mc1);
        await db.SaveChangesAsync();

        db.Set<ModuleCaseObservation>().Add(new ModuleCaseObservation
        {
            CaseId = mc1.CaseId,
            Outcome = (int)ObservationOutcome.Found,
            Verified = true,
            RetrievedUtc = obsCompletedTime
        });
        await db.SaveChangesAsync();

        var currencyAfterObs = await service.GetDataCurrencySummaryAsync(reqId);
        Assert.Equal(obsCompletedTime, currencyAfterObs.AsOfUtc);
        Assert.Equal(Ist.Format(obsCompletedTime, "dd-MMM-yyyy, HH:mm"), currencyAfterObs.AsOfIst);
        Assert.True(currencyAfterObs.RefreshInProgress);
    }

    [Fact]
    public async Task DossierAssembler_Aligns_DataCurrency_With_Displayed_Workbook_Litigations()
    {
        await using var db = CreateContext();
        var ingestionSnapshotTime = new DateTime(2026, 10, 5, 8, 0, 0, DateTimeKind.Utc);
        var matchedObsTime = new DateTime(2026, 10, 12, 11, 30, 0, DateTimeKind.Utc);
        var unmatchedObsTime = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

        var client = new Client { ClientCode = "TST" + Guid.NewGuid().ToString("N")[..8], ClientName = "Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var req = new McaRequest
        {
            Client = client,
            EntityType = EntityType.Company,
            CompanyName = "Test Co",
            RequestNumber = $"REQ-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(req);
        await db.SaveChangesAsync();
        long reqId = req.RequestId;

        var ingestionRun = new IngestionRun
        {
            RequestId = reqId,
            RunNumber = 1,
            StartedDate = ingestionSnapshotTime.AddMinutes(-5),
            CompletedDate = ingestionSnapshotTime,
            SourceSnapshotDate = ingestionSnapshotTime
        };
        db.IngestionRuns.Add(ingestionRun);
        await db.SaveChangesAsync();

        req.LatestCompletedIngestionRunId = ingestionRun.IngestionRunId;
        var analysisRun = new AnalysisRun
        {
            RequestId = reqId,
            IngestionRunId = ingestionRun.IngestionRunId,
            RunNumber = 1,
            Status = AnalysisRunStatus.Completed,
            StartedDate = ingestionSnapshotTime,
            CompletedDate = ingestionSnapshotTime.AddMinutes(1)
        };
        db.AnalysisRuns.Add(analysisRun);
        await db.SaveChangesAsync();

        // Dossier register (db.Litigations for ingestionRun) has 4 cases:
        // 1. WP 101/2022 at Delhi High Court -> unique 1-to-1 match with refreshed court case
        // 2. CS 202/2023 at Bombay High Court -> no matching court case
        // 3. OA 901/2021 with missing Court -> MUST NOT match lcUnrelated1 even though OA 901/2021 exists in court cases
        // 4. OA 902/2021 at DRT Chennai -> MUST NOT match because two court cases share OA 902/2021 at DRT Chennai (ambiguous)
        db.Litigations.AddRange(
            new Litigation
            {
                RequestId = reqId,
                IngestionRunId = ingestionRun.IngestionRunId,
                CaseNumber = "WP 101/2022",
                CaseType = "WP",
                CaseStatus = "Pending",
                Court = "Delhi High Court",
                Litigants = "Bank vs Test Co"
            },
            new Litigation
            {
                RequestId = reqId,
                IngestionRunId = ingestionRun.IngestionRunId,
                CaseNumber = "CS 202/2023",
                CaseType = "CS",
                CaseStatus = "Pending",
                Court = "Bombay High Court",
                Litigants = "Vendor vs Test Co"
            },
            new Litigation
            {
                RequestId = reqId,
                IngestionRunId = ingestionRun.IngestionRunId,
                CaseNumber = "OA 901/2021",
                CaseType = "OA",
                CaseStatus = "Pending",
                Court = null,
                Litigants = "Missing Court vs Test Co"
            },
            new Litigation
            {
                RequestId = reqId,
                IngestionRunId = ingestionRun.IngestionRunId,
                CaseNumber = "OA 902/2021",
                CaseType = "OA",
                CaseStatus = "Pending",
                Court = "DRT Chennai",
                Litigants = "Ambiguous vs Test Co"
            });

        var futureDate = DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(1)).ToString("yyyy-MM-dd");
        var lcMatched = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "101/2022",
            CaseType = "WP",
            CaseYear = "2022",
            CaseStatus = "Disposed",
            Court = "Delhi High Court",
            State = "Delhi",
            District = "New Delhi",
            FilingDate = "2022-01-01",
            LastHearingDate = "2026-10-10",
            NextHearingDate = futureDate
        };
        var lcMissingCourtCounterpart = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "901/2021",
            CaseType = "OA",
            CaseYear = "2021",
            CaseStatus = "Disposed",
            Court = "DRT Mumbai",
            State = "Maharashtra",
            District = "Mumbai",
            FilingDate = "2021-01-01",
            LastHearingDate = "2026-10-01",
            NextHearingDate = futureDate
        };
        var lcAmbiguous1 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "902/2021",
            CaseType = "OA",
            CaseYear = "2021",
            CaseStatus = "Disposed",
            Court = "DRT Chennai",
            State = "Tamil Nadu",
            District = "Chennai",
            FilingDate = "2021-02-01",
            LastHearingDate = "2026-10-02",
            NextHearingDate = futureDate
        };
        var lcAmbiguous2 = new LitigationCase
        {
            RequestId = reqId,
            CaseNumber = "902/2021",
            CaseType = "OA",
            CaseYear = "2021",
            CaseStatus = "Pending",
            Court = "DRT Chennai"
        };
        db.LitigationCases.AddRange(lcMatched, lcMissingCourtCounterpart, lcAmbiguous1, lcAmbiguous2);
        await db.SaveChangesAsync();

        var mcMatched = new ModuleCase { LegacyLitigationCaseId = lcMatched.LitigationCaseId, CaseKey = "K-M101" };
        var mcUn1 = new ModuleCase { LegacyLitigationCaseId = lcMissingCourtCounterpart.LitigationCaseId, CaseKey = "K-U901" };
        var mcUn2 = new ModuleCase { LegacyLitigationCaseId = lcAmbiguous1.LitigationCaseId, CaseKey = "K-U902" };
        db.Set<ModuleCase>().AddRange(mcMatched, mcUn1, mcUn2);
        await db.SaveChangesAsync();

        db.Set<ModuleCaseLive>().AddRange(
            new ModuleCaseLive { CaseId = mcMatched.CaseId, LastRetrievedUtc = matchedObsTime },
            new ModuleCaseLive { CaseId = mcUn1.CaseId, LastRetrievedUtc = unmatchedObsTime },
            new ModuleCaseLive { CaseId = mcUn2.CaseId, LastRetrievedUtc = unmatchedObsTime });
        db.Set<ModuleCaseObservation>().AddRange(
            new ModuleCaseObservation { CaseId = mcMatched.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = matchedObsTime },
            new ModuleCaseObservation { CaseId = mcUn1.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = unmatchedObsTime },
            new ModuleCaseObservation { CaseId = mcUn2.CaseId, Outcome = (int)ObservationOutcome.Found, Verified = true, RetrievedUtc = unmatchedObsTime });
        await db.SaveChangesAsync();

        var statusService = new CourtRefreshStatusService(db);
        var assembler = new DossierAssembler(db, courtRefreshStatus: statusService);
        var dossier = await assembler.BuildAsync(reqId);

        Assert.NotNull(dossier);
        Assert.Equal(4, dossier.Litigation.All.Count);
        Assert.NotNull(dossier.Litigation.DataCurrency);

        // Only the 1 unambiguous 1-to-1 matched case with complete number + court is counted as refreshed
        Assert.Equal(4, dossier.Litigation.DataCurrency.TotalCases);
        Assert.Equal(1, dossier.Litigation.DataCurrency.RefreshedCasesCount);
        Assert.Equal(3, dossier.Litigation.DataCurrency.UnrefreshedCasesCount);
        Assert.Equal(matchedObsTime, dossier.Litigation.DataCurrency.AsOfUtc);

        var matchedDossierRow = Assert.Single(dossier.Litigation.All, l => l.CaseNumber == "WP 101/2022");
        Assert.Equal("Disposed", matchedDossierRow.CaseStatus);

        // Missing-court row and ambiguous duplicate-number row must NOT have another case's status applied
        var missingCourtRow = Assert.Single(dossier.Litigation.All, l => l.CaseNumber == "OA 901/2021");
        Assert.Equal("Pending", missingCourtRow.CaseStatus);

        var ambiguousRow = Assert.Single(dossier.Litigation.All, l => l.CaseNumber == "OA 902/2021");
        Assert.Equal("Pending", ambiguousRow.CaseStatus);
    }
}
