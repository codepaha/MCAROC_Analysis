using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
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
using Microsoft.Extensions.Caching.Memory;
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

    private static RequestsController CreateRequestsController(AppDbContext db, bool isReviewer, FullTextSearchStatus? fullTextStatus = null)
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
            tokenService: null!,
            fullTextStatus: fullTextStatus);

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

        // Litigation is client-facing: no endpoint may be pinned to the internal-reviewer scheme.
        // Portal login still applies through the app's fallback authorization policy.
        foreach (var name in new[] { "StartSearch", "GetSearchStatus", "DownloadOrderDocument", "DownloadOrdersZip", "DownloadPdfReport", "DownloadCsvReport", "StartAnalysis", "GetAnalysis" })
        {
            var method = controllerType.GetMethod(name);
            Assert.NotNull(method);
            Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(), a => a.AuthenticationSchemes == "InternalReviewer");
        }

        // Search still mutates state and paid spend, so it keeps CSRF protection.
        Assert.NotNull(controllerType.GetMethod("StartSearch")!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        // So does the paid AI analysis POST; the tab's fetch sends the token in the request body.
        Assert.NotNull(controllerType.GetMethod("StartAnalysis")!.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
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
            bprOptions: unconfiguredOpts,
            starter: null!,
            logger: NullLogger<LitigationController>.Instance);

        var result = await controller.StartSearch(request.RequestId, default);
        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("not configured", badRequest.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<McaRequest> SeedRequestWithCompletedReportAsync(AppDbContext db, bool withReport)
    {
        var client = new Client { ClientCode = "RFC" + Guid.NewGuid().ToString("N")[..6], ClientName = "Refresh Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Refresh Co", EntityType = EntityType.Company, RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        if (withReport)
        {
            var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "hash_refresh_1" };
            db.LitigationSearchJobs.Add(job);
            await db.SaveChangesAsync();
            db.LitigationReportSnapshots.Add(new LitigationReportSnapshot
            {
                LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = "hash_refresh_1",
                Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddDays(-30), CasesPersistedCount = 1
            });
            await db.SaveChangesAsync();
        }
        return request;
    }

    private static LitigationController JsonController(AppDbContext db)
    {
        var controller = new LitigationController(
            db: db, env: new FakeEnv(Path.GetTempPath()),
            bprOptions: Options.Create(new BprLitigationOptions { BaseUrl = "https://bpr.example/", Id = "app", SecretKey = "secret" }),
            starter: null!, logger: NullLogger<LitigationController>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers.Accept = "application/json";
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    [Fact]
    public async Task StartSearch_RefusesARefresh_ThatWasNotConfirmedAsChargeable()
    {
        await using var db = CreateContext();
        var request = await SeedRequestWithCompletedReportAsync(db, withReport: true);

        var result = await JsonController(db).StartSearch(request.RequestId, default);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("chargeable", conflict.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await db.LitigationSearchJobs.CountAsync(j => j.RequestId == request.RequestId));
    }

    [Fact]
    public async Task StartSearch_ConfirmedRefresh_PassesTheChargeableGuard()
    {
        await using var db = CreateContext();
        var request = await SeedRequestWithCompletedReportAsync(db, withReport: true);

        // starter is null: getting past the guard and the refresh policy reaches it, which is all this asserts.
        await Assert.ThrowsAsync<NullReferenceException>(() =>
            JsonController(db).StartSearch(request.RequestId, default, confirmChargeableRefresh: true));
    }

    [Fact]
    public async Task StartSearch_FirstSearch_NeedsNoChargeableConfirmation()
    {
        await using var db = CreateContext();
        var request = await SeedRequestWithCompletedReportAsync(db, withReport: false);

        await Assert.ThrowsAsync<NullReferenceException>(() => JsonController(db).StartSearch(request.RequestId, default));
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
            bprOptions: configuredOpts,
            starter: null!,
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
            bprOptions: configuredOpts,
            starter: null!,
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
            RequestId = request.RequestId,
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
            RequestId = request.RequestId,
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
            RequestId = request.RequestId,
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
            bprOptions: Options.Create(new BprLitigationOptions()),
            starter: null!,
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
            RequestId = request.RequestId,
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

                // Directly assert shared compiled delegates
                Assert.Equal(tc.Expected == LitigationCaseStatusBucket.Disposed, LitigationCaseStatusClassifier.IsDisposedCompiled(caseObj));
                Assert.Equal(tc.Expected == LitigationCaseStatusBucket.Pending, LitigationCaseStatusClassifier.IsPendingCompiled(caseObj));
                Assert.Equal(tc.Expected == LitigationCaseStatusBucket.Unknown, LitigationCaseStatusClassifier.IsUnknownCompiled(caseObj));
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task StatusClassifier_SqlExpressions_ExecuteAgainstSqlServer_WithZeroClientEvaluation()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "SQLT" + Guid.NewGuid().ToString("N")[..6], ClientName = "SQL Trans Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "SQL Trans Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var cases = new List<LitigationCase>
        {
            new() { RequestId = request.RequestId, CaseNumber = "C1", CaseStatus = "DISPOSED AFTER HEARING", Court = "Delhi HC", FirstSeenUtc = DateTime.UtcNow },
            new() { RequestId = request.RequestId, CaseNumber = "C2", CaseStatus = "pEnDiNg", Court = "Delhi HC", FirstSeenUtc = DateTime.UtcNow },
            new() { RequestId = request.RequestId, CaseNumber = "C3", CaseStatus = "Arbitrary Unmatched", Court = "Delhi HC", FirstSeenUtc = DateTime.UtcNow },
            new() { RequestId = request.RequestId, CaseNumber = "C4", CaseStatus = null, CaseStage = null, Court = "Delhi HC", FirstSeenUtc = DateTime.UtcNow },
            new() { RequestId = request.RequestId, CaseNumber = "C5", CaseStatus = "Admitted", CaseStage = "Hearing", Court = "Delhi HC", FirstSeenUtc = DateTime.UtcNow }
        };
        db.LitigationCases.AddRange(cases);
        await db.SaveChangesAsync();

        // 1. Verify IsDisposedExpr translates to SQL and executes
        var disposedInSql = await db.LitigationCases
            .Where(c => c.RequestId == request.RequestId)
            .Where(LitigationCaseStatusClassifier.IsDisposedExpr)
            .ToListAsync();
        Assert.Single(disposedInSql);
        Assert.Equal("C1", disposedInSql[0].CaseNumber);

        // 2. Verify IsPendingExpr translates to SQL and executes
        var pendingInSql = await db.LitigationCases
            .Where(c => c.RequestId == request.RequestId)
            .Where(LitigationCaseStatusClassifier.IsPendingExpr)
            .ToListAsync();
        Assert.Equal(2, pendingInSql.Count);
        Assert.Contains(pendingInSql, c => c.CaseNumber == "C2");
        Assert.Contains(pendingInSql, c => c.CaseNumber == "C5");

        // 3. Verify IsUnknownExpr translates to SQL and executes
        var unknownInSql = await db.LitigationCases
            .Where(c => c.RequestId == request.RequestId)
            .Where(LitigationCaseStatusClassifier.IsUnknownExpr)
            .ToListAsync();
        Assert.Equal(2, unknownInSql.Count);
        Assert.Contains(unknownInSql, c => c.CaseNumber == "C3");
        Assert.Contains(unknownInSql, c => c.CaseNumber == "C4");

        // 4. Parity verification: every case in database matches the compiled delegate exactly
        var allCasesInDb = await db.LitigationCases.Where(c => c.RequestId == request.RequestId).ToListAsync();
        foreach (var c in allCasesInDb)
        {
            var isDisposedSql = disposedInSql.Any(x => x.LitigationCaseId == c.LitigationCaseId);
            var isPendingSql = pendingInSql.Any(x => x.LitigationCaseId == c.LitigationCaseId);
            var isUnknownSql = unknownInSql.Any(x => x.LitigationCaseId == c.LitigationCaseId);

            Assert.Equal(isDisposedSql, LitigationCaseStatusClassifier.IsDisposedCompiled(c));
            Assert.Equal(isPendingSql, LitigationCaseStatusClassifier.IsPendingCompiled(c));
            Assert.Equal(isUnknownSql, LitigationCaseStatusClassifier.IsUnknownCompiled(c));

            var expectedBucket = isDisposedSql ? LitigationCaseStatusBucket.Disposed :
                                 isPendingSql ? LitigationCaseStatusBucket.Pending :
                                 LitigationCaseStatusBucket.Unknown;
            Assert.Equal(expectedBucket, LitigationCaseStatusClassifier.Classify(c));
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

        var snapPrior = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = "hash_prior", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow.AddDays(-2), CasesPersistedCount = 1 };
        var snapAuth = new LitigationReportSnapshot { LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = "hash_auth", Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CasesPersistedCount = 1 };
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
    }

    // ── 9. Client-facing rendering: no reviewer gate ─────────────────────────

    private static RequestDetailsViewModel ClientModel(LitigationTabViewModel lake, List<Litigation>? workbook = null) => new()
    {
        Request = new McaRequest { RequestId = 7, CompanyName = "Client View Co", RequestNumber = "REQ-CLIENT-1" },
        Documents = [],
        Litigations = workbook ?? [],
        LitigationDataLake = lake
    };

    [Fact]
    public async Task RenderLitigationTab_Client_HasNoReviewerGate_AndPromptsSearchWhenNotStarted()
    {
        var html = await RenderTabAsync(ClientModel(new LitigationTabViewModel { Request = new McaRequest { CompanyName = "Client View Co" } }));

        Assert.DoesNotContain("/internal/login", html);
        Assert.DoesNotContain("Internal Reviewer", html);
        Assert.Contains("has not started yet", html);
        Assert.DoesNotContain("sec-litigation-probable", html);
        Assert.DoesNotContain("sec-litigation-unverified", html);
    }

    [Fact]
    public async Task RenderLitigationTab_Client_ShowsBothReportVariants_AnalysisButtonAndRefreshAllowance()
    {
        var lake = new LitigationTabViewModel
        {
            Request = new McaRequest { RequestId = 7, CompanyName = "Client View Co" },
            SearchJob = new LitigationSearchJob { Status = LitigationSearchJobStatus.Completed },
            AuthoritativeSnapshot = new LitigationReportSnapshot { RetrievedUtc = DateTime.UtcNow.AddDays(-3) },
            HasCompletedAnalysis = true,
            NeedsAnalysis = true,
            RefreshIntervalDays = 15,
            Refresh = LitigationRefreshPolicy.Evaluate(2, DateTime.UtcNow.AddDays(-3), DateTime.UtcNow, 3, 15)
        };

        var html = await RenderTabAsync(ClientModel(lake));

        Assert.Contains("/Requests/7/Litigation/Report/pdf\"", html);
        Assert.Contains("/Requests/7/Litigation/Report/csv\"", html);
        Assert.Contains("Report/pdf?withAnalysis=true", html);
        Assert.Contains("Report/csv?withAnalysis=true", html);
        Assert.Contains("litigation-run-analysis", html);
        Assert.Contains("__RequestVerificationToken", html);
        Assert.Contains("2 of 3 refreshes remaining", html.Replace("\r", "").Replace("\n", " ").Replace("  ", " "));
        Assert.DoesNotContain("Internal Reviewer", html);
    }

    [Fact]
    public async Task RenderLitigationTab_Client_DisablesAnalysisWhenNothingNewToAnalyse()
    {
        var lake = new LitigationTabViewModel
        {
            Request = new McaRequest { RequestId = 7, CompanyName = "Client View Co" },
            SearchJob = new LitigationSearchJob { Status = LitigationSearchJobStatus.Completed },
            AuthoritativeSnapshot = new LitigationReportSnapshot { RetrievedUtc = DateTime.UtcNow },
            HasCompletedAnalysis = true,
            NeedsAnalysis = false
        };

        var html = await RenderTabAsync(ClientModel(lake));
        var idx = html.IndexOf("litigation-run-analysis", StringComparison.Ordinal);
        Assert.True(idx >= 0);
        Assert.Contains("disabled", html.Substring(idx, 200));
    }

    [Fact]
    public async Task RenderLitigationTab_Client_WorkbookFallbackShowsConfirmedOnly()
    {
        var workbook = new List<Litigation>
        {
            new() { MatchStatus = LitigationMatchStatus.Confirmed, CaseNumber = "CONF-1", Court = "Delhi HC" },
            new() { MatchStatus = LitigationMatchStatus.Probable, CaseNumber = "PROB-1", Court = "Delhi HC" },
            new() { MatchStatus = LitigationMatchStatus.Uncertain, CaseNumber = "UNVER-1", Court = "Delhi HC" }
        };
        var html = await RenderTabAsync(ClientModel(new LitigationTabViewModel { Request = new McaRequest { CompanyName = "Client View Co" } }, workbook));

        Assert.Contains("CONF-1", html);
        Assert.DoesNotContain("PROB-1", html);
        Assert.DoesNotContain("UNVER-1", html);
    }

    [Theory]
    [InlineData(0, 0, true, 3)]    // nothing bought yet: the first search is free
    [InlineData(1, 20, true, 3)]   // initial search 20 days ago: refresh allowed
    [InlineData(1, 5, false, 3)]   // inside the 15-day gap
    [InlineData(3, 40, true, 1)]   // two refreshes used, one left
    [InlineData(4, 400, false, 0)] // three used: capped however long ago
    public void RefreshPolicy_EnforcesCapAndSpacing(int committed, int daysSinceLast, bool allowed, int remaining)
    {
        var now = DateTime.UtcNow;
        var state = LitigationRefreshPolicy.Evaluate(committed, committed == 0 ? null : now.AddDays(-daysSinceLast), now, 3, 15);
        Assert.Equal(allowed, state.Allowed);
        Assert.Equal(remaining, state.Remaining);
    }

    [Fact]
    public async Task RenderLitigationTab_RendersOutcomeChipsAndFineAmounts_WithLowConfidenceStyling()
    {
        var lake = new LitigationTabViewModel
        {
            Request = new McaRequest { RequestId = 42, CompanyName = "Litigation Outcomes Co" },
            SearchJob = new LitigationSearchJob { Status = LitigationSearchJobStatus.Completed },
            AuthoritativeSnapshot = new LitigationReportSnapshot { RetrievedUtc = DateTime.UtcNow },
            TotalCaseCount = 1,
            TotalClassifiedOrdersCount = 2,
            OutcomeCounts = new Dictionary<LitigationOrderOutcome, int>
            {
                [LitigationOrderOutcome.StayGranted] = 1,
                [LitigationOrderOutcome.FinePenalty] = 1
            },
            Cases =
            [
                new LitigationCaseCardViewModel
                {
                    LitigationCaseId = 101,
                    CaseNumber = "HC-101/2022",
                    Court = "High Court",
                    Orders =
                    [
                        new LitigationOrderRowViewModel
                        {
                            LitigationCaseOrderId = 201,
                            LitigationOrderDocumentId = 301,
                            OrderDate = "2022-04-15",
                            OrderType = "Interim Order",
                            DocumentStatus = LitigationOrderDocumentStatus.Downloaded,
                            Outcomes = [LitigationOrderOutcome.StayGranted],
                            Confidence = ClassificationConfidence.High
                        },
                        new LitigationOrderRowViewModel
                        {
                            LitigationCaseOrderId = 202,
                            LitigationOrderDocumentId = 302,
                            OrderDate = "2022-05-20",
                            OrderType = "Final Order",
                            DocumentStatus = LitigationOrderDocumentStatus.Downloaded,
                            Outcomes = [LitigationOrderOutcome.FinePenalty],
                            FineAmount = 50000m,
                            Confidence = ClassificationConfidence.Low,
                            EvidenceTruncated = true
                        }
                    ]
                }
            ]
        };

        var html = await RenderTabAsync(ClientModel(lake));

        // Outcome chips bar
        Assert.Contains("id=\"litigation-outcome-chips\"", html);
        Assert.Contains("All Outcomes (2)", html);
        Assert.Contains("Stay Granted (1)", html);
        Assert.Contains("Fine / Penalty (1)", html);

        // Case card orders table
        Assert.Contains("<th>Outcomes</th>", html);
        Assert.Contains("Stay Granted", html);
        Assert.Contains("Fine / Penalty", html);
        Assert.Contains("₹50,000", html);
        Assert.Contains("opacity-75 fst-italic", html);
        Assert.Contains("Low Confidence", html);
        Assert.Contains("Truncated", html);

        // Download link
        Assert.Contains("/Requests/7/Litigation/Orders/301/download", html);
    }

    [Fact]
    public async Task RenderLitigationTab_PreservesOutcomeFilterInFormAndPagination()
    {
        var lake = new LitigationTabViewModel
        {
            Request = new McaRequest { RequestId = 42, CompanyName = "Litigation Outcomes Co" },
            SearchJob = new LitigationSearchJob { Status = LitigationSearchJobStatus.Completed },
            AuthoritativeSnapshot = new LitigationReportSnapshot { RetrievedUtc = DateTime.UtcNow },
            TotalCaseCount = 50,
            PageSize = 25,
            CurrentPage = 1,
            SelectedOutcome = "StayGranted",
            OutcomeCounts = new Dictionary<LitigationOrderOutcome, int>
            {
                [LitigationOrderOutcome.StayGranted] = 5
            },
            Cases =
            [
                new LitigationCaseCardViewModel
                {
                    LitigationCaseId = 101,
                    CaseNumber = "HC-101/2022",
                    Orders = []
                }
            ]
        };

        var html = await RenderTabAsync(ClientModel(lake));

        Assert.Contains("name=\"outcome\"", html);
        Assert.Contains("value=\"StayGranted\"", html);
        Assert.Contains("All Outcomes", html);
        Assert.Contains("Stay Granted (5)", html);
    }

    [Fact]
    public async Task Details_FiltersByOutcome_WhenOutcomeQueryParamIsProvided()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "OUT" + Guid.NewGuid().ToString("N")[..6], ClientName = "Outcome Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Outcome Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "hash_auth" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();

        var snap = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId,
            RequestId = request.RequestId,
            ReportHash = "hash_auth",
            Status = LitigationReportSnapshotStatus.Completed,
            RetrievedUtc = DateTime.UtcNow,
            CasesPersistedCount = 2
        };
        db.LitigationReportSnapshots.Add(snap);
        await db.SaveChangesAsync();

        var case1 = new LitigationCase { RequestId = request.RequestId, CaseNumber = "CASE-STAY", Court = "High Court", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow };
        var case2 = new LitigationCase { RequestId = request.RequestId, CaseNumber = "CASE-FINE", Court = "High Court", CaseStatus = "Disposed", FirstSeenUtc = DateTime.UtcNow };
        db.LitigationCases.AddRange(case1, case2);
        await db.SaveChangesAsync();

        db.LitigationCaseSourceReports.AddRange(
            new LitigationCaseSourceReport { LitigationCaseId = case1.LitigationCaseId, LitigationReportSnapshotId = snap.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow },
            new LitigationCaseSourceReport { LitigationCaseId = case2.LitigationCaseId, LitigationReportSnapshotId = snap.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow }
        );
        await db.SaveChangesAsync();

        var order1 = new LitigationCaseOrder { LitigationCaseId = case1.LitigationCaseId, OrderDate = "2023-01-10", OrderType = "Interim Order", CreatedUtc = DateTime.UtcNow };
        var order2 = new LitigationCaseOrder { LitigationCaseId = case2.LitigationCaseId, OrderDate = "2023-02-15", OrderType = "Final Order", CreatedUtc = DateTime.UtcNow };
        db.LitigationCaseOrders.AddRange(order1, order2);
        await db.SaveChangesAsync();

        var doc1 = new LitigationOrderDocument { LitigationCaseOrderId = order1.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(10), CreatedUtc = DateTime.UtcNow };
        var doc2 = new LitigationOrderDocument { LitigationCaseOrderId = order2.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(10), CreatedUtc = DateTime.UtcNow };
        db.LitigationOrderDocuments.AddRange(doc1, doc2);
        await db.SaveChangesAsync();

        var chunk1 = new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = doc1.LitigationOrderDocumentId, LitigationCaseOrderId = order1.LitigationCaseOrderId,
            LitigationCaseId = case1.LitigationCaseId, CaseNumber = case1.CaseNumber, Court = case1.Court, OrderDate = order1.OrderDate,
            ChunkIndex = 0, PageNumber = 1, ChunkText = "Interim stay granted on recovery proceedings.",
            Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
        };
        var chunk2 = new LitigationOrderChunk
        {
            RequestId = request.RequestId, LitigationOrderDocumentId = doc2.LitigationOrderDocumentId, LitigationCaseOrderId = order2.LitigationCaseOrderId,
            LitigationCaseId = case2.LitigationCaseId, CaseNumber = case2.CaseNumber, Court = case2.Court, OrderDate = order2.OrderDate,
            ChunkIndex = 0, PageNumber = 1, ChunkText = "Petition dismissed with penalty of Rs. 10,000.",
            Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
        };
        db.LitigationOrderChunks.AddRange(chunk1, chunk2);
        await db.SaveChangesAsync();

        var h1 = LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence([chunk1]));
        var h2 = LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence([chunk2]));

        var run = new LitigationAiAnalysisRun { RequestId = request.RequestId, Status = LitigationAiAnalysisRunStatus.Completed, RunNumber = 1, CreatedUtc = DateTime.UtcNow };
        db.LitigationAiAnalysisRuns.Add(run);
        await db.SaveChangesAsync();

        db.LitigationOrderClassifications.AddRange(
            new LitigationOrderClassification
            {
                RequestId = request.RequestId, LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId,
                LitigationCaseId = case1.LitigationCaseId, LitigationCaseOrderId = order1.LitigationCaseOrderId,
                LitigationOrderDocumentId = doc1.LitigationOrderDocumentId, Status = LitigationAiAnalysisItemStatus.Completed,
                OutcomeTypesJson = "[\"StayGranted\"]", Confidence = ClassificationConfidence.High,
                EvidenceHash = h1.EvidenceHash, PromptHash = h1.PromptHash
            },
            new LitigationOrderClassification
            {
                RequestId = request.RequestId, LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId,
                LitigationCaseId = case2.LitigationCaseId, LitigationCaseOrderId = order2.LitigationCaseOrderId,
                LitigationOrderDocumentId = doc2.LitigationOrderDocumentId, Status = LitigationAiAnalysisItemStatus.Completed,
                OutcomeTypesJson = "[\"FinePenalty\"]", FineAmount = 10000m, Confidence = ClassificationConfidence.Medium,
                EvidenceHash = h2.EvidenceHash, PromptHash = h2.PromptHash
            }
        );
        await db.SaveChangesAsync();

        var controller = CreateRequestsController(db, isReviewer: true);

        // 1. Query for StayGranted
        var resStay = await controller.Details(request.RequestId, charge: null, court: null, status: null, page: 1, outcome: "StayGranted");
        var lakeStay = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(resStay).Model).LitigationDataLake!;

        Assert.Equal(1, lakeStay.TotalCaseCount);
        Assert.Equal(case1.LitigationCaseId, Assert.Single(lakeStay.Cases).LitigationCaseId);
        Assert.Equal("StayGranted", lakeStay.SelectedOutcome);
        Assert.Equal(1, lakeStay.OutcomeCounts[LitigationOrderOutcome.StayGranted]);
        Assert.Equal(1, lakeStay.OutcomeCounts[LitigationOrderOutcome.FinePenalty]);
        Assert.Equal(2, lakeStay.TotalClassifiedOrdersCount);
        Assert.Equal([LitigationOrderOutcome.StayGranted], lakeStay.Cases[0].Orders[0].Outcomes);

        // 2. Query for FinePenalty
        var resFine = await controller.Details(request.RequestId, charge: null, court: null, status: null, page: 1, outcome: "FinePenalty");
        var lakeFine = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(resFine).Model).LitigationDataLake!;

        Assert.Equal(1, lakeFine.TotalCaseCount);
        Assert.Equal(case2.LitigationCaseId, Assert.Single(lakeFine.Cases).LitigationCaseId);
        Assert.Equal(10000m, lakeFine.Cases[0].Orders[0].FineAmount);
        Assert.Equal([LitigationOrderOutcome.FinePenalty], lakeFine.Cases[0].Orders[0].Outcomes);
    }

    /// <summary>The lookup's matches only carry orders with an outcome, so the order rows take their classification status
    /// from the lookup's current statuses: an InsufficientEvidence order reads "Outcome unclear", not the "—" of an order
    /// that was never classified.</summary>
    [Fact]
    public async Task Details_DistinguishesInsufficientEvidenceOrders_FromUnclassifiedOnes()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "UNC" + Guid.NewGuid().ToString("N")[..6], ClientName = "Unclear Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "Unclear Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new LitigationSearchJob { RequestId = request.RequestId, Status = LitigationSearchJobStatus.Completed, RawResponseHash = "hash_unc" };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync();
        var snap = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = request.RequestId, ReportHash = "hash_unc",
            Status = LitigationReportSnapshotStatus.Completed, RetrievedUtc = DateTime.UtcNow, CasesPersistedCount = 1
        };
        db.LitigationReportSnapshots.Add(snap);
        var lc = new LitigationCase { RequestId = request.RequestId, CaseNumber = "CASE-UNC", Court = "High Court", CaseStatus = "Pending", FirstSeenUtc = DateTime.UtcNow };
        db.LitigationCases.Add(lc);
        await db.SaveChangesAsync();
        db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport { LitigationCaseId = lc.LitigationCaseId, LitigationReportSnapshotId = snap.LitigationReportSnapshotId, FirstSeenUtc = DateTime.UtcNow });

        var run = new LitigationAiAnalysisRun { RequestId = request.RequestId, Status = LitigationAiAnalysisRunStatus.Completed, RunNumber = 1, CreatedUtc = DateTime.UtcNow };
        db.LitigationAiAnalysisRuns.Add(run);
        await db.SaveChangesAsync();

        // Three orders: a stay, one whose text didn't show what it decided, and one never classified.
        var orderIds = new List<long>();
        foreach (var (date, text, status, outcomes) in new (string, string, LitigationAiAnalysisItemStatus?, string)[]
        {
            ("2023-03-01", "Interim stay granted.", LitigationAiAnalysisItemStatus.Completed, "[\"StayGranted\"]"),
            ("2023-02-01", "Illegible order sheet.", LitigationAiAnalysisItemStatus.InsufficientEvidence, "[]"),
            ("2023-01-01", "Listed for hearing.", null, "[]")
        })
        {
            var order = new LitigationCaseOrder { LitigationCaseId = lc.LitigationCaseId, OrderDate = date, OrderType = "Order", CreatedUtc = DateTime.UtcNow };
            db.LitigationCaseOrders.Add(order);
            await db.SaveChangesAsync();
            var doc = new LitigationOrderDocument { LitigationCaseOrderId = order.LitigationCaseOrderId, Status = LitigationOrderDocumentStatus.Downloaded, RetainedUntilUtc = DateTime.UtcNow.AddDays(10), CreatedUtc = DateTime.UtcNow };
            db.LitigationOrderDocuments.Add(doc);
            await db.SaveChangesAsync();
            var chunk = new LitigationOrderChunk
            {
                RequestId = request.RequestId, LitigationOrderDocumentId = doc.LitigationOrderDocumentId, LitigationCaseOrderId = order.LitigationCaseOrderId,
                LitigationCaseId = lc.LitigationCaseId, CaseNumber = lc.CaseNumber, Court = lc.Court, OrderDate = date,
                ChunkIndex = 0, PageNumber = 1, ChunkText = text,
                Embedding = new SqlVector<float>(new float[768]), EmbeddingModel = "test", EmbeddingDimensions = 768, ChunkingVersion = "1.0", CreatedDate = DateTime.UtcNow
            };
            db.LitigationOrderChunks.Add(chunk);
            if (status is { } st)
            {
                var h = LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence([chunk]));
                db.LitigationOrderClassifications.Add(new LitigationOrderClassification
                {
                    RequestId = request.RequestId, LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, LitigationCaseId = lc.LitigationCaseId,
                    LitigationCaseOrderId = order.LitigationCaseOrderId, LitigationOrderDocumentId = doc.LitigationOrderDocumentId,
                    Status = st, OutcomeTypesJson = outcomes, Confidence = ClassificationConfidence.Medium,
                    EvidenceHash = h.EvidenceHash, PromptHash = h.PromptHash
                });
            }
            await db.SaveChangesAsync();
            orderIds.Add(order.LitigationCaseOrderId);
        }

        var controller = CreateRequestsController(db, isReviewer: true);
        var model = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(
            await controller.Details(request.RequestId, charge: null, court: null, status: null, page: 1, outcome: null)).Model);
        var rows = Assert.Single(model.LitigationDataLake!.Cases).Orders.ToDictionary(o => o.LitigationCaseOrderId);

        Assert.Equal(LitigationAiAnalysisItemStatus.Completed, rows[orderIds[0]].ClassificationStatus);
        Assert.Equal(LitigationAiAnalysisItemStatus.InsufficientEvidence, rows[orderIds[1]].ClassificationStatus);
        Assert.Empty(rows[orderIds[1]].Outcomes);
        Assert.Null(rows[orderIds[2]].ClassificationStatus);

        var html = await RenderTabAsync(ClientModel(model.LitigationDataLake!));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, ">Outcome unclear<"));
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

    private sealed class FixedFullTextStatus(FullTextSearchState state)
        : FullTextSearchStatus(null!, new MemoryCache(new MemoryCacheOptions()))
    {
        public override Task<FullTextSearchState> GetAsync(CancellationToken ct) => Task.FromResult(state);
    }

    /// <summary>#349: the Details page tells the chat panel when keyword search is off on this server.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Details_FlagsKeywordSearchUnavailable_OnlyWhenFullTextIsMissing(bool fullTextAvailable, bool expectedFlag)
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "FTS" + Guid.NewGuid().ToString("N")[..6], ClientName = "FTS Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest { Client = client, CompanyName = "FTS Co", RequestNumber = $"REQ-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var state = new FullTextSearchState(fullTextAvailable, fullTextAvailable, fullTextAvailable);

        var controller = CreateRequestsController(db, isReviewer: true, new FixedFullTextStatus(state));
        var model = Assert.IsType<RequestDetailsViewModel>(Assert.IsType<ViewResult>(
            await controller.Details(request.RequestId, charge: null)).Model);

        Assert.Equal(expectedFlag, model.KeywordSearchUnavailable);
    }
}
