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

    // ── 8. Non-Reviewer Non-Leakage Rendering Test ───────────────────────────

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
