using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.Dashboard;
using MCAROC_Analysis.Services.Dossier;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LitigationTabAndControllerTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class FakeAuthService(bool isReviewer) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            if (isReviewer && scheme == "InternalReviewer")
            {
                var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "InternalReviewer")], "InternalReviewer");
                var principal = new ClaimsPrincipal(identity);
                var ticket = new AuthenticationTicket(principal, "InternalReviewer");
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static RequestsController CreateRequestsController(AppDbContext db, bool isReviewer)
    {
        var env = new FakeEnv(Path.GetTempPath());
        var fileVal = new FileValidationService(new ExcelSheetReader());
        var derivService = new WorkbookDerivativeService(db, env, fileVal, NullLogger<WorkbookDerivativeService>.Instance);

        var controller = new RequestsController(
            db: db,
            orchestrator: null!,
            fileValidation: fileVal,
            analysisQueue: null!,
            filingQueue: null!,
            requestListQueryService: null!,
            dossierCache: Dossier.DossierGoldenMasterTests.CreateCache(),
            env: env,
            corporateTimelineBuilder: new CorporateTimelineBuilder(db),
            logger: NullLogger<RequestsController>.Instance,
            derivativeService: derivService,
            tokenService: null!);

        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthService(isReviewer));
        var sp = services.BuildServiceProvider();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        controller.TempData = new TempDataDictionary(httpContext, new NullTempDataProvider());

        return controller;
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    // ── 1. LitigationCaseStatusClassifier Tests ──────────────────────────────

    [Theory]
    [InlineData("Pending", null, LitigationCaseStatusBucket.Pending)]
    [InlineData("Admitted", "Evidence Stage", LitigationCaseStatusBucket.Pending)]
    [InlineData("Notice Issued", null, LitigationCaseStatusBucket.Pending)]
    [InlineData("Stay Granted", "Hearing", LitigationCaseStatusBucket.Pending)]
    [InlineData("Disposed", null, LitigationCaseStatusBucket.Disposed)]
    [InlineData("Dismissed", "Final Order", LitigationCaseStatusBucket.Disposed)]
    [InlineData("Settled", null, LitigationCaseStatusBucket.Disposed)]
    [InlineData("Decree Passed", null, LitigationCaseStatusBucket.Disposed)]
    [InlineData("Closed", null, LitigationCaseStatusBucket.Disposed)]
    [InlineData(null, null, LitigationCaseStatusBucket.Unknown)]
    [InlineData("", "", LitigationCaseStatusBucket.Unknown)]
    [InlineData("Arbitrary String 123", "Arbitrary 456", LitigationCaseStatusBucket.Unknown)]
    public void StatusClassifier_CorrectlyBucketsCases(string? status, string? stage, LitigationCaseStatusBucket expected)
    {
        var actual = LitigationCaseStatusClassifier.Classify(status, stage);
        Assert.Equal(expected, actual);
    }

    // ── 2. Court Summary Grid Reconciliation & Paging Tests ─────────────────

    [Fact]
    public void CourtSummaryGrid_ReconcilesPendingDisposedUnknown()
    {
        var grid = new LitigationCourtSummaryGrid
        {
            Rows =
            [
                new LitigationCourtSummaryRow { CourtName = "Delhi HC", TotalCases = 10, PendingCases = 6, DisposedCases = 3, UnknownCases = 1, TotalOrders = 15 },
                new LitigationCourtSummaryRow { CourtName = "Bombay HC", TotalCases = 5, PendingCases = 2, DisposedCases = 2, UnknownCases = 1, TotalOrders = 7 },
                new LitigationCourtSummaryRow { CourtName = "NCLT", TotalCases = 20, PendingCases = 15, DisposedCases = 4, UnknownCases = 1, TotalOrders = 30 }
            ]
        };

        Assert.Equal(35, grid.TotalCases);
        Assert.Equal(23, grid.TotalPendingCases);
        Assert.Equal(9, grid.TotalDisposedCases);
        Assert.Equal(3, grid.TotalUnknownCases);
        Assert.Equal(52, grid.TotalOrders);
        Assert.True(grid.IsReconciled);
    }

    [Fact]
    public void Pagination_ReconciliationInvariantFollowsTotalCaseCountNotPageCount()
    {
        var grid = new LitigationCourtSummaryGrid
        {
            Rows =
            [
                new LitigationCourtSummaryRow { CourtName = "Court A", TotalCases = 50, PendingCases = 30, DisposedCases = 15, UnknownCases = 5 },
                new LitigationCourtSummaryRow { CourtName = "Court B", TotalCases = 37, PendingCases = 20, DisposedCases = 12, UnknownCases = 5 }
            ]
        };

        var vm = new LitigationTabViewModel
        {
            Request = new McaRequest { CompanyName = "Test Co" },
            CourtSummaryGrid = grid,
            TotalCaseCount = 87,
            CurrentPage = 2,
            PageSize = 25,
            Cases = new List<LitigationCaseCardViewModel>(new LitigationCaseCardViewModel[25])
        };

        Assert.Equal(87, grid.TotalCases);
        Assert.Equal(87, vm.TotalCaseCount);
        Assert.Equal(25, vm.Cases.Count);
        Assert.Equal(4, vm.TotalPages);
        Assert.True(grid.IsReconciled);
    }

    // ── 3. Security, Audit, and Anti-Forgery Attributes Tests ─────────────────

    [Fact]
    public void LitigationEndpoints_HaveCorrectSecurityAttributes()
    {
        var controllerType = typeof(LitigationController);

        var startSearchMethod = controllerType.GetMethod("StartSearch");
        Assert.NotNull(startSearchMethod);
        var searchAuth = startSearchMethod.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(searchAuth);
        Assert.Equal("InternalReviewer", searchAuth.AuthenticationSchemes);
        Assert.NotNull(startSearchMethod.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());

        var statusMethod = controllerType.GetMethod("GetSearchStatus");
        Assert.NotNull(statusMethod);
        var statusAuth = statusMethod.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(statusAuth);
        Assert.Equal("InternalReviewer", statusAuth.AuthenticationSchemes);

        var downloadMethod = controllerType.GetMethod("DownloadOrderDocument");
        Assert.NotNull(downloadMethod);
        var downloadAuth = downloadMethod.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(downloadAuth);
        Assert.Equal("InternalReviewer", downloadAuth.AuthenticationSchemes);
    }

    [Fact]
    public void AuditRouteRegistry_ContainsLitigationStartSearch()
    {
        Assert.True(AuditRouteRegistry.RouteMap.ContainsKey(("Litigation", "StartSearch")));
        var resolved = AuditRouteRegistry.Resolve("Litigation", "StartSearch");
        Assert.Equal(AuditActionType.LitigationSearchRequested, resolved.ActionType);
    }

    // ── 4. Eligibility Gate & Input Validation Tests ─────────────────────────

    [Fact]
    public async Task StartSearch_FailsClosed_WhenBprNotConfigured()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "UNCF" + Guid.NewGuid().ToString("N")[..6], ClientName = "Unconfigured BPR Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Unconf Co", EntityType = EntityType.Company, RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var unconfiguredOpts = Options.Create(new BprLitigationOptions { BaseUrl = "", Id = "", SecretKey = "" });
        var controller = new LitigationController(
            db: db,
            env: new FakeEnv(Path.GetTempPath()),
            analysis: null!,
            searchJobService: null!,
            searchQueue: new LitigationSearchQueue(),
            bprOptions: unconfiguredOpts,
            logger: NullLogger<LitigationController>.Instance);

        var result = await controller.StartSearch(request.RequestId, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not configured", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartSearch_RejectsBlankCompanyName()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "BLNK" + Guid.NewGuid().ToString("N")[..6], ClientName = "Blank Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "   ", EntityType = EntityType.Company, RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var configuredOpts = Options.Create(new BprLitigationOptions { BaseUrl = "https://bpr.test", Id = "id", SecretKey = "secret" });
        var controller = new LitigationController(
            db: db,
            env: new FakeEnv(Path.GetTempPath()),
            analysis: null!,
            searchJobService: null!,
            searchQueue: new LitigationSearchQueue(),
            bprOptions: configuredOpts,
            logger: NullLogger<LitigationController>.Instance);

        var result = await controller.StartSearch(request.RequestId, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Company name is required", badRequest.Value?.ToString());
    }

    [Fact]
    public async Task StartSearch_RejectsUnsupportedEntityType()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "UNSP" + Guid.NewGuid().ToString("N")[..6], ClientName = "Unsupported Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Trust Entity", EntityType = (EntityType)999, RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var configuredOpts = Options.Create(new BprLitigationOptions { BaseUrl = "https://bpr.test", Id = "id", SecretKey = "secret" });
        var controller = new LitigationController(
            db: db,
            env: new FakeEnv(Path.GetTempPath()),
            analysis: null!,
            searchJobService: null!,
            searchQueue: new LitigationSearchQueue(),
            bprOptions: configuredOpts,
            logger: NullLogger<LitigationController>.Instance);

        var result = await controller.StartSearch(request.RequestId, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Unsupported entity type", badRequest.Value?.ToString());
    }

    // ── 5. Snapshot Partitioning & Mutable Metadata Regression Test ─────────

    [Fact]
    public async Task Details_PartitionsCasesToAuthoritativeSnapshot_WhileRenderingCurrentMutableMetadata()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "PART" + Guid.NewGuid().ToString("N")[..6], ClientName = "Partition Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client,
            CompanyName = "Partition Test Enterprise",
            EntityType = EntityType.Company,
            RequestNumber = $"REQ-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            Status = LitigationSearchJobStatus.Polling,
            RawResponseHash = "hash_snapshot_2",
            ProgressPercent = 50,
            StatusMessage = "Polling BPR report"
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        // Snapshot 1: Completed prior run
        var snapshot1 = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash_snapshot_1",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow.AddDays(-2),
            CasesPersistedCount = 1
        };
        db.LitigationReportSnapshots.Add(snapshot1);

        // Snapshot 2: In-progress current run
        var snapshot2 = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash_snapshot_2",
            Status = LitigationReportSnapshotStatus.InProgress,
            RetrievedUtc = DateTime.UtcNow,
            CasesPersistedCount = 2
        };
        db.LitigationReportSnapshots.Add(snapshot2);
        await db.SaveChangesAsync();

        // Case A was found in Snapshot 1, later updated with Snapshot 2 data + Order 2
        var caseA = new LitigationCase
        {
            RequestId = request.RequestId,
            Court = "Delhi High Court",
            CaseNumber = "DHC-101",
            CaseStatus = "Disposed", // Mutated on rerun from Pending to Disposed
            FirstSeenUtc = DateTime.UtcNow.AddDays(-2),
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(caseA);
        await db.SaveChangesAsync();

        var order1 = new LitigationCaseOrder { LitigationCaseId = caseA.LitigationCaseId, OrderDate = "2026-09-01", OrderType = "Order" };
        var order2 = new LitigationCaseOrder { LitigationCaseId = caseA.LitigationCaseId, OrderDate = "2026-09-20", OrderType = "Final Decree" }; // Added on rerun
        db.LitigationCaseOrders.AddRange(order1, order2);

        // Source reports: Case A linked to Snapshot 1 and Snapshot 2
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = caseA.LitigationCaseId,
            LitigationReportSnapshotId = snapshot1.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow.AddDays(-2)
        });
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = caseA.LitigationCaseId,
            LitigationReportSnapshotId = snapshot2.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow
        });

        // Case B: Brand new case surfaced ONLY in Snapshot 2 (current in-progress run)
        var caseB = new LitigationCase
        {
            RequestId = request.RequestId,
            Court = "Bombay High Court",
            CaseNumber = "BHC-202",
            CaseStatus = "Pending",
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        db.LitigationCases.Add(caseB);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = caseB.LitigationCaseId,
            LitigationReportSnapshotId = snapshot2.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        // Exercise Details as InternalReviewer
        var controller = CreateRequestsController(db, isReviewer: true);
        var result = await controller.Details(request.RequestId, charge: null);
        var viewResult = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<RequestDetailsViewModel>(viewResult.Model);

        var lake = vm.LitigationDataLake;
        Assert.NotNull(lake);
        Assert.True(lake.IsReviewer);
        Assert.True(lake.IsPriorRunDataShown);
        Assert.Equal(snapshot1.LitigationReportSnapshotId, lake.AuthoritativeSnapshot?.LitigationReportSnapshotId);
        Assert.False(lake.SourceCoverage.IsAuthoritativeCoverage);

        // Crucial assertions:
        // 1. Exactly 1 case card (Case A). Case B from partial Snapshot 2 is strictly excluded.
        Assert.Single(lake.Cases);
        var card = lake.Cases[0];
        Assert.Equal("DHC-101", card.CaseNumber);

        // 2. Mutable metadata check: Case A exhibits current database values (Disposed status) and Order 2
        Assert.Equal(LitigationCaseStatusBucket.Disposed, card.StatusBucket);
        Assert.Equal(2, card.Orders.Count);

        // 3. Grid aggregates match the authoritative snapshot (1 case, Delhi High Court)
        Assert.Equal(1, lake.CourtSummaryGrid.TotalCases);
        Assert.Equal(1, lake.CourtSummaryGrid.TotalDisposedCases);
        Assert.Equal(0, lake.CourtSummaryGrid.TotalPendingCases);
    }

    // ── 6. AI Analysis Stale Detection Test ──────────────────────────────────

    [Fact]
    public async Task Details_FlagsAnalysisAsStale_WhenCaseMutatedAfterAnalysisCompleted()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "STLE" + Guid.NewGuid().ToString("N")[..6], ClientName = "Stale AI Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Stale Test Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash_stale_1"
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash_stale_1",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        var caseRow = new LitigationCase
        {
            RequestId = request.RequestId,
            Court = "High Court",
            CaseNumber = "HC-99",
            FirstSeenUtc = DateTime.UtcNow.AddDays(-5),
            LastSeenUtc = DateTime.UtcNow // Case re-surfaced today
        };
        db.LitigationCases.Add(caseRow);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
        {
            LitigationCaseId = caseRow.LitigationCaseId,
            LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
            FirstSeenUtc = DateTime.UtcNow.AddDays(-5)
        });

        // AI Run completed yesterday (before today's LastSeenUtc)
        var aiRun = new LitigationAiAnalysisRun
        {
            RequestId = request.RequestId,
            RunNumber = 1,
            Status = LitigationAiAnalysisRunStatus.Completed,
            CompletedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.LitigationAiAnalysisRuns.Add(aiRun);
        await db.SaveChangesAsync();

        var caseAi = new LitigationCaseAiAnalysis
        {
            LitigationAiAnalysisRunId = aiRun.LitigationAiAnalysisRunId,
            LitigationCaseId = caseRow.LitigationCaseId,
            Status = LitigationAiAnalysisItemStatus.Completed,
            AnalysisJson = "{\"summary\":\"No immediate exposure found.\",\"unknowns\":[],\"evidenceReferences\":[]}",
            CompletedUtc = DateTime.UtcNow.AddDays(-1)
        };
        db.LitigationCaseAiAnalyses.Add(caseAi);
        await db.SaveChangesAsync();

        var controller = CreateRequestsController(db, isReviewer: true);
        var result = await controller.Details(request.RequestId, charge: null);
        var viewResult = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<RequestDetailsViewModel>(viewResult.Model);

        var card = Assert.Single(vm.LitigationDataLake!.Cases);
        Assert.NotNull(card.Analysis);
        Assert.True(card.IsAnalysisStaleComparedToCase);
    }

    // ── 7. Terminal SnapshotMissing Status Test ──────────────────────────────

    [Fact]
    public async Task GetSearchStatus_ReturnsSnapshotMissingAndTerminal_WhenSnapshotNotFound()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "MISS" + Guid.NewGuid().ToString("N")[..6], ClientName = "Missing Snapshot Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Missing Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash_never_saved_snapshot",
            ProgressPercent = 100
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var controller = new LitigationController(
            db: db,
            env: new FakeEnv(Path.GetTempPath()),
            analysis: null!,
            searchJobService: null!,
            searchQueue: new LitigationSearchQueue(),
            bprOptions: Options.Create(new BprLitigationOptions()),
            logger: NullLogger<LitigationController>.Instance);

        var httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var result = await controller.GetSearchStatus(request.RequestId, default);
        var okResult = Assert.IsType<OkObjectResult>(result);

        var dict = okResult.Value?.GetType().GetProperties()
            .ToDictionary(p => p.Name, p => p.GetValue(okResult.Value));
        Assert.NotNull(dict);
        Assert.Equal("SnapshotMissing", dict["importState"]);
        Assert.Equal(true, dict["isTerminal"]);
        Assert.Equal(false, dict["isFullyIndexed"]);

        // Response Cache-Control header check
        Assert.Equal("no-store, private", httpContext.Response.Headers.CacheControl.ToString());
    }

    // ── 8. SQL Provider Filtered Grid, Precedence & Pagination ────────────────

    [Fact]
    public async Task Details_SqlProvider_FilteredGrid_PrecedenceAndPagination()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "SQLP" + Guid.NewGuid().ToString("N")[..6], ClientName = "SQL Provider Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client,
            CompanyName = "SQL Provider Co",
            EntityType = EntityType.Company,
            RequestNumber = $"REQ-{Guid.NewGuid():N}",
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob
        {
            RequestId = request.RequestId,
            Status = LitigationSearchJobStatus.Completed,
            RawResponseHash = "hash_auth_full_graph",
            ProgressPercent = 100
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            ReportHash = "hash_auth_full_graph",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow.AddHours(-2),
            CasesPersistedCount = 26
        };
        db.LitigationReportSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        // Case 1: null court, mixed-case pending
        var case1 = new LitigationCase { RequestId = request.RequestId, Court = null, CaseNumber = "CASE-1", CaseStatus = "pEnDiNg", FirstSeenUtc = DateTime.UtcNow };
        // Case 2: whitespace court, unknown status
        var case2 = new LitigationCase { RequestId = request.RequestId, Court = "   ", CaseNumber = "CASE-2", CaseStatus = "Arbitrary 123", FirstSeenUtc = DateTime.UtcNow };
        // Case 3: "Delhi HC", overlapping tokens "DISPOSED AFTER HEARING" (Disposed precedence)
        var case3 = new LitigationCase { RequestId = request.RequestId, Court = "Delhi HC", CaseNumber = "CASE-3", CaseStatus = "DISPOSED AFTER HEARING", FirstSeenUtc = DateTime.UtcNow };
        // Case 4: "Delhi HC  " trailing space, pending trial
        var case4 = new LitigationCase { RequestId = request.RequestId, Court = "Delhi HC  ", CaseNumber = "CASE-4", CaseStatus = "Pending Trial", FirstSeenUtc = DateTime.UtcNow };

        db.LitigationCases.AddRange(case1, case2, case3, case4);

        // Cases 5..26 (22 additional cases) to test 25-case page boundary (26 total cases)
        var extraCases = new List<LitigationCase>();
        for (int i = 5; i <= 26; i++)
        {
            extraCases.Add(new LitigationCase
            {
                RequestId = request.RequestId,
                Court = null,
                CaseNumber = $"CASE-{i}",
                CaseStatus = "Pending",
                FirstSeenUtc = DateTime.UtcNow
            });
        }
        db.LitigationCases.AddRange(extraCases);
        await db.SaveChangesAsync();

        var allCases = new[] { case1, case2, case3, case4 }.Concat(extraCases).ToList();
        foreach (var c in allCases)
        {
            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
            {
                LitigationCaseId = c.LitigationCaseId,
                LitigationReportSnapshotId = snapshot.LitigationReportSnapshotId,
                FirstSeenUtc = DateTime.UtcNow
            });
        }

        // Add order and document for Case 3
        var order = new LitigationCaseOrder { LitigationCaseId = case3.LitigationCaseId, OrderDate = "2026-01-15", OrderType = "Final Order" };
        db.LitigationCaseOrders.Add(order);
        await db.SaveChangesAsync();

        var orderDoc = new LitigationOrderDocument
        {
            LitigationCaseOrderId = order.LitigationCaseOrderId,
            Status = LitigationOrderDocumentStatus.Downloaded,
            StoragePath = "C:\\fake\\order.pdf"
        };
        db.LitigationOrderDocuments.Add(orderDoc);
        await db.SaveChangesAsync();

        var controller = CreateRequestsController(db, isReviewer: true);

        // Call 1: Unfiltered (page 1)
        var res1 = await controller.Details(request.RequestId, charge: null);
        var vm1 = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(res1).Model);
        var lake1 = vm1.LitigationDataLake!;
        Assert.Equal(26, lake1.TotalCaseCount);
        Assert.Equal(25, lake1.Cases.Count);

        // Verify Grid Invariants on Call 1
        var grid1 = lake1.CourtSummaryGrid;
        Assert.Equal(26, grid1.TotalCases);
        Assert.True(grid1.IsReconciled);
        Assert.Equal(grid1.TotalCases, grid1.TotalPendingCases + grid1.TotalDisposedCases + grid1.TotalUnknownCases);
        Assert.Equal(grid1.TotalCases, lake1.TotalCaseCount);
        Assert.Equal(2, grid1.Rows.Count); // Collapsed into "Delhi HC" and "Unspecified Court"
        foreach (var r in grid1.Rows)
        {
            Assert.Equal(r.TotalCases, r.PendingCases + r.DisposedCases + r.UnknownCases);
        }

        var dhcRow = grid1.Rows.Single(r => r.CourtName == "Delhi HC");
        Assert.Equal(2, dhcRow.TotalCases);
        Assert.Equal(1, dhcRow.DisposedCases);
        Assert.Equal(1, dhcRow.PendingCases);
        Assert.Equal(0, dhcRow.UnknownCases);

        var unspecRow = grid1.Rows.Single(r => r.CourtName == "Unspecified Court");
        Assert.Equal(24, unspecRow.TotalCases);

        // Verify Case Cards on Call 1 are ordered by normalized court ("Delhi HC" before "Unspecified Court")
        Assert.Equal("Delhi HC", lake1.Cases[0].Court);
        Assert.Equal("Delhi HC  ", lake1.Cases[1].Court);

        // Call 2: Filtered by "Unspecified Court"
        var res2 = await controller.Details(request.RequestId, charge: null, court: "Unspecified Court");
        var lake2 = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(res2).Model).LitigationDataLake!;
        Assert.Equal(24, lake2.TotalCaseCount);
        var grid2 = lake2.CourtSummaryGrid;
        Assert.Single(grid2.Rows);
        Assert.Equal(24, grid2.TotalCases);
        Assert.Equal(grid2.TotalCases, grid2.TotalPendingCases + grid2.TotalDisposedCases + grid2.TotalUnknownCases);
        Assert.Equal(grid2.TotalCases, lake2.TotalCaseCount);
        foreach (var r in grid2.Rows)
        {
            Assert.Equal(r.TotalCases, r.PendingCases + r.DisposedCases + r.UnknownCases);
        }

        // Call 3: Filtered by "Delhi HC" + "Disposed"
        var res3 = await controller.Details(request.RequestId, charge: null, court: "Delhi HC", status: "Disposed");
        var lake3 = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(res3).Model).LitigationDataLake!;
        Assert.Equal(1, lake3.TotalCaseCount);
        var card3 = Assert.Single(lake3.Cases);
        Assert.Equal("CASE-3", card3.CaseNumber);
        Assert.Equal(LitigationCaseStatusBucket.Disposed, card3.StatusBucket);
        var grid3 = lake3.CourtSummaryGrid;
        Assert.Single(grid3.Rows);
        Assert.Equal(1, grid3.TotalCases);
        Assert.Equal(1, grid3.TotalDisposedCases);
        Assert.Equal(0, grid3.TotalPendingCases);
        Assert.Equal(0, grid3.TotalUnknownCases);
        Assert.Equal(grid3.TotalCases, grid3.TotalPendingCases + grid3.TotalDisposedCases + grid3.TotalUnknownCases);
        Assert.Equal(grid3.TotalCases, lake3.TotalCaseCount);
        foreach (var r in grid3.Rows)
        {
            Assert.Equal(r.TotalCases, r.PendingCases + r.DisposedCases + r.UnknownCases);
        }

        // Call 4: Page 2
        var res4 = await controller.Details(request.RequestId, charge: null, page: 2);
        var lake4 = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(res4).Model).LitigationDataLake!;
        Assert.Single(lake4.Cases); // The 26th case
        Assert.Equal(26, lake4.TotalCaseCount);
        Assert.Equal(26, lake4.CourtSummaryGrid.TotalCases);
        Assert.True(lake4.CourtSummaryGrid.IsReconciled);
        Assert.Equal(lake4.CourtSummaryGrid.TotalCases, lake4.CourtSummaryGrid.TotalPendingCases + lake4.CourtSummaryGrid.TotalDisposedCases + lake4.CourtSummaryGrid.TotalUnknownCases);
        foreach (var r in lake4.CourtSummaryGrid.Rows)
        {
            Assert.Equal(r.TotalCases, r.PendingCases + r.DisposedCases + r.UnknownCases);
        }
    }

    [Fact]
    public void StatusClassifier_AsciiContract_MatchesAcrossCulturesAndCompiledDelegates()
    {
        var testCases = new (string? Status, string? Stage, LitigationCaseStatusBucket Expected)[]
        {
            ("DISPOSED AFTER HEARING", null, LitigationCaseStatusBucket.Disposed),
            ("pEnDiNg", null, LitigationCaseStatusBucket.Pending),
            ("AdMiTtEd", "EvIdEnCe StAgE", LitigationCaseStatusBucket.Pending),
            ("DiSmIsSeD", "Final Order", LitigationCaseStatusBucket.Disposed),
            ("Arbitrary 123", "Unknown 456", LitigationCaseStatusBucket.Unknown),
            (null, null, LitigationCaseStatusBucket.Unknown),
            ("", "", LitigationCaseStatusBucket.Unknown)
        };

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            // Test with Turkish culture where 'I' normally maps to 'ı' (U+0131)
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            foreach (var tc in testCases)
            {
                var bucket = LitigationCaseStatusClassifier.Classify(tc.Status, tc.Stage);
                Assert.Equal(tc.Expected, bucket);

                var caseObj = new LitigationCase { CaseStatus = tc.Status, CaseStage = tc.Stage };
                var bucketFromCase = LitigationCaseStatusClassifier.Classify(caseObj);
                Assert.Equal(tc.Expected, bucketFromCase);
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task Details_SourceCoverage_IsStrictlyScopedToAuthoritativeSnapshot()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "SCOP" + Guid.NewGuid().ToString("N")[..6], ClientName = "Scope Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Scope Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "hash_auth" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snapPrior = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, ReportHash = "hash_prior", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddDays(-2), CasesPersistedCount = 1 };
        var snapAuth = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, ReportHash = "hash_auth", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CasesPersistedCount = 1 };
        db.LitigationReportSnapshots.AddRange(snapPrior, snapAuth);
        await db.SaveChangesAsync();

        var caseA = new LitigationCase { RequestId = request.RequestId, Court = "High Court", CaseNumber = "CASE-A", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(caseA);
        await db.SaveChangesAsync();

        // 2 observations for Case A: 1 in prior snapshot, 1 in authoritative snapshot
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = caseA.LitigationCaseId, LitigationReportSnapshotId = snapPrior.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow.AddDays(-2) });
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = caseA.LitigationCaseId, LitigationReportSnapshotId = snapAuth.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var controller = CreateRequestsController(db, isReviewer: true);
        var res = await controller.Details(request.RequestId, charge: null);
        var lake = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(res).Model).LitigationDataLake!;

        // UniqueCasesCount == 1, TotalObservationsCount strictly == 1 (from snapAuth, NOT 2)
        Assert.Equal(1, lake.SourceCoverage.UniqueCasesCount);
        Assert.Equal(1, lake.SourceCoverage.TotalObservationsCount);
        Assert.Equal(2, lake.SourceCoverage.CompletedSnapshotHistory.Count);
    }

    [Fact]
    public async Task Details_DoesNotCrash_WhenAuthenticationServiceIsNotRegisteredInContext()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "NOAU" + Guid.NewGuid().ToString("N")[..6], ClientName = "No Auth Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "No Auth Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var controller = CreateRequestsController(db, isReviewer: false);
        // Clear RequestServices to simulate test context without IAuthenticationService
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext()
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

        var result = await controller.Details(request.RequestId, charge: null);
        var viewResult = Assert.IsType<ViewResult>(result);
        var vm = Assert.IsType<RequestDetailsViewModel>(viewResult.Model);
        Assert.NotNull(vm.LitigationDataLake);
        Assert.False(vm.LitigationDataLake.IsReviewer);
    }

    // ── 9. Non-Reviewer Non-Leakage Rendering Test ───────────────────────────

    [Fact]
    public async Task RenderLitigationTab_NonReviewer_ShowsLoginPromptAndZeroDataLakeData()
    {
        var model = new RequestDetailsViewModel
        {
            Request = new McaRequest { CompanyName = "Leakage Guard Co", RequestNumber = "REQ-LEAK-1" },
            Documents = [],
            Litigations = [],
            LitigationDataLake = new LitigationTabViewModel
            {
                Request = new McaRequest { CompanyName = "Leakage Guard Co" },
                IsReviewer = false
            }
        };

        var html = await RenderTabAsync(model);

        // Shows internal login prompt
        Assert.Contains("/internal/login", html);
        Assert.Contains("Internal Reviewer Access Required", html);

        // Must NOT leak data lake sensitive elements
        Assert.DoesNotContain("Court Summary Grid", html);
        Assert.DoesNotContain("Evidence-Grounded AI Analysis", html);
        Assert.DoesNotContain("CSP ID:", html);
        Assert.DoesNotContain("/Litigation/Orders/", html);
        Assert.DoesNotContain("Snapshot membership anchored", html);
    }

    private static async Task<string> RenderTabAsync(RequestDetailsViewModel model)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        var repoRoot = dir?.FullName ?? throw new DirectoryNotFoundException("Repo root not found");
        var webAppDir = Path.Combine(repoRoot, "MCAROC.Portal", "MCAROC_Analysis");
        var webRoot = Path.Combine(webAppDir, "wwwroot");

        var services = new ServiceCollection();
        var env = new FakeEnv(webAppDir) { WebRootPath = webRoot };
        services.AddSingleton<IWebHostEnvironment>(env);
        services.AddSingleton<ObjectPoolProvider, DefaultObjectPoolProvider>();
        var diag = new System.Diagnostics.DiagnosticListener("Microsoft.AspNetCore");
        services.AddSingleton<System.Diagnostics.DiagnosticSource>(diag);
        services.AddSingleton<System.Diagnostics.DiagnosticListener>(diag);
        services.AddSingleton(System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All));
        services.AddLogging();
        services.AddControllersWithViews();

        var sp = services.BuildServiceProvider();
        var viewEngine = sp.GetRequiredService<IRazorViewEngine>();
        var tempDataProvider = sp.GetRequiredService<ITempDataProvider>();

        var httpContext = new DefaultHttpContext { RequestServices = sp };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());

        var viewPath = "/Views/Requests/Details/_LitigationTab.cshtml";
        var viewResult = viewEngine.GetView(executingFilePath: null, viewPath: viewPath, isMainPage: false);
        if (!viewResult.Success)
        {
            viewResult = viewEngine.FindView(actionContext, "Details/_LitigationTab", isMainPage: false);
        }

        await using var writer = new StringWriter();
        var viewDictionary = new ViewDataDictionary<RequestDetailsViewModel>(
            new EmptyModelMetadataProvider(),
            new ModelStateDictionary())
        {
            Model = model
        };

        var viewContext = new ViewContext(
            actionContext,
            viewResult.View,
            viewDictionary,
            new TempDataDictionary(httpContext, tempDataProvider),
            writer,
            new HtmlHelperOptions());

        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }
}
