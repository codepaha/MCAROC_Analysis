using System.Text.Json;
using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Unit and integration tests for the UI half of issue #294 (interactive search, name-only intake)
/// and issue #295 (ambiguity queue, "Select this CIN" board action).</summary>
public class PipelineAmbiguityQueueAndIntakeTests : IAsyncLifetime
{
    private static readonly string ConnectionString = TestDatabase.ConnectionString;
    private readonly string _token = "ZZ" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _serial = Random.Shared.Next(100_000, 999_999).ToString();
    private readonly List<long> _requests = [];

    private string Cin(int n) => $"U{n}0000MH2020PTC{_serial}";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static PipelineReconciler Reconciler(AppDbContext db) =>
        new(db, new PipelineSnapshotReader(db, new ConfigurationBuilder().Build(), Options.Create(new BprLitigationOptions()), Options.Create(new ReferenceToolOptions())), TimeProvider.System, NullLogger<PipelineReconciler>.Instance);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        if (_requests.Count > 0)
        {
            var reqIds = _requests;
            await db.PipelineEvents.Where(e => db.PipelineRuns.Any(r => reqIds.Contains(r.RequestId) && r.PipelineRunId == e.PipelineRunId)).ExecuteDeleteAsync();
            await db.PipelineStageStates.Where(s => db.PipelineRuns.Any(r => reqIds.Contains(r.RequestId) && r.PipelineRunId == s.PipelineRunId)).ExecuteDeleteAsync();
            await db.PipelineRuns.Where(r => reqIds.Contains(r.RequestId)).ExecuteDeleteAsync();
            await db.AutoFetchJobs.Where(j => reqIds.Contains(j.RequestId)).ExecuteDeleteAsync();
            await db.IdentityResolutions.Where(r => r.RequestId != null && reqIds.Contains(r.RequestId.Value)).ExecuteDeleteAsync();
            await db.Requests.Where(r => reqIds.Contains(r.RequestId)).ExecuteDeleteAsync();
        }
        await db.CompanyNameTokens.Where(t => t.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
        await db.CompanyMasterRecords.Where(r => r.Identifier.EndsWith(_serial)).ExecuteDeleteAsync();
    }

    private async Task SeedMasterAsync(params (string Identifier, string Name, string? State, string? District)[] rows)
    {
        await using (var db = CreateContext())
        {
            foreach (var (id, name, state, district) in rows)
            {
                db.CompanyMasterRecords.Add(new CompanyMasterRecord
                {
                    Identifier = id,
                    RecordType = CompanyMasterRecordType.Company,
                    Name = name,
                    Status = "Active",
                    State = state,
                    District = district,
                    RegistrationDate = new DateOnly(2020, 5, 10),
                    Category = "Company limited by Shares",
                    Class = "Private",
                    ListingStatus = "Unlisted"
                });
            }
            await db.SaveChangesAsync();
        }
        await using var connection = new SqlConnection(ConnectionString);
        await CompanyMasterNameBackfill.RunAsync(connection, onlyMissing: true);
        await CompanyNameTokenIndex.RebuildAsync(connection);
    }

    private (AutoFetchController Controller, AutoFetchQueue Queue, IdentityResolutionService Identity, AppDbContext Db) CreateAutoFetch(bool autoSelect = false)
    {
        var db = CreateContext();
        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc"
        });
        var client = new ReferenceToolClient(new HttpClient(new NoNetworkHandler()), options, new ReferenceToolSession(), new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);
        var queue = new AutoFetchQueue();
        var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(), null!,
            new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
        var resolverOptions = Options.Create(new ResolverOptions { AutoSelectEnabled = autoSelect });
        var identity = new IdentityResolutionService(db, resolverOptions);
        var adopter = new PipelineAdopter(db, new StaticOptionsMonitor(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce }), TimeProvider.System, NullLogger<PipelineAdopter>.Instance);
        var httpContext = new DefaultHttpContext();
        var controller = new AutoFetchController(db, jobs, queue, client, options, NullLogger<AutoFetchController>.Instance, adopter, null, null, identity)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
        return (controller, queue, identity, db);
    }

    private (PipelineController Controller, AutoFetchQueue Queue, IdentitySelectionService Selection, AppDbContext Db) CreatePipeline(AutoFetchJobService jobs, AutoFetchQueue queue, IdentityResolutionService identity)
    {
        var db = CreateContext();
        var reconciler = Reconciler(db);
        var selection = new IdentitySelectionService(db, identity, jobs, queue, TimeProvider.System, NullLogger<IdentitySelectionService>.Instance, reconciler);
        var optionsMonitor = new StaticOptionsMonitor(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce });
        var refOptions = Options.Create(new ReferenceToolOptions { BaseUrl = "https://reference-tool.test", SessionCookie = "PHPSESSID=abc" });
        var httpContext = new DefaultHttpContext();
        var controller = new PipelineController(db, optionsMonitor, selection, jobs, queue, refOptions)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new NullTempDataProvider())
        };
        return (controller, queue, selection, db);
    }

    [Fact]
    public async Task Search_WithMasterDataAndHints_ReturnsRankedCandidatesWithDisambiguators()
    {
        var cin1 = Cin(1);
        var cin2 = Cin(2);
        await SeedMasterAsync(
            (cin1, $"{_token} SOLUTIONS PRIVATE LIMITED", "Maharashtra", "Mumbai"),
            (cin2, $"{_token} SERVICES PRIVATE LIMITED", "Karnataka", "Bengaluru"));

        var (controller, _, _, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            var result = Assert.IsType<OkObjectResult>(await controller.Search(_token, CancellationToken.None, state: "Maharashtra", district: "Mumbai"));
            var candidates = Assert.IsAssignableFrom<IEnumerable<CompanySearchCandidateDto>>(result.Value).ToList();

            Assert.NotEmpty(candidates);
            var top = candidates.First();
            Assert.Equal(cin1, top.Identifier);
            Assert.Equal($"{_token} SOLUTIONS PRIVATE LIMITED", top.Name);
            Assert.Equal("Active", top.Status);
            Assert.Equal("Maharashtra", top.State);
            Assert.Equal("Mumbai", top.District);
            Assert.True(top.MatchPercent > 0);
            Assert.NotEmpty(top.Reasons);
        }
    }

    [Fact]
    public async Task Intake_NameOnly_CreatesRequestAndAdoptsPipelineRun_WithoutJob()
    {
        var (controller, queue, _, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            var model = new AutoFetchRequestViewModel
            {
                ClientId = 1,
                CompanyName = $"{_token} UNREGISTERED VENTURE",
                EntityType = EntityType.Company,
                EntityTypeConfirmed = true
            };

            var actionResult = await controller.New(model, CancellationToken.None);
            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            Assert.Equal("Details", redirect.ActionName);
            var reqId = Assert.IsType<long>(redirect.RouteValues!["id"]);
            _requests.Add(reqId);

            await using var check = CreateContext();
            var req = await check.Requests.SingleAsync(r => r.RequestId == reqId);
            Assert.Equal($"{_token} UNREGISTERED VENTURE", req.CompanyName);
            Assert.Null(req.Cin);
            Assert.Null(req.AutoFetchCompanyIdentifier);

            // No job created since CIN was unresolved
            Assert.False(await check.AutoFetchJobs.AnyAsync(j => j.RequestId == reqId));
            Assert.False(queue.TryRead(out _));

            // Pipeline adopted
            var run = await check.PipelineRuns.FirstOrDefaultAsync(r => r.RequestId == reqId);
            Assert.NotNull(run);
            await Reconciler(check).ReconcileAsync(run.PipelineRunId, CancellationToken.None);

            var reloadedRun = await check.PipelineRuns.AsNoTracking().SingleAsync(r => r.PipelineRunId == run.PipelineRunId);
            Assert.Equal(PipelineOutcome.NeedsAttention, reloadedRun.Outcome);

            var resolveStage = await check.PipelineStageStates.AsNoTracking().FirstOrDefaultAsync(s => s.PipelineRunId == run.PipelineRunId && s.Stage == PipelineStage.Resolve);
            Assert.NotNull(resolveStage);
            Assert.Equal(PipelineStageStateKind.NeedsAttention, resolveStage.State);
        }
    }

    [Fact]
    public async Task Intake_WithSelectedIdentifier_RecordsHumanSelectedResolution()
    {
        var cin = Cin(1);
        await SeedMasterAsync((cin, $"{_token} ENTERPRISES PRIVATE LIMITED", "Delhi", "Delhi"));

        var (controller, queue, _, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            var model = new AutoFetchRequestViewModel
            {
                ClientId = 1,
                CompanyName = $"{_token} ENTERPRISES PRIVATE LIMITED",
                Cin = cin,
                SelectedIdentifier = cin,
                EntityType = EntityType.Company,
                EntityTypeConfirmed = true
            };

            var actionResult = await controller.New(model, CancellationToken.None);
            var redirect = Assert.IsType<RedirectToActionResult>(actionResult);
            var reqId = Assert.IsType<long>(redirect.RouteValues!["id"]);
            _requests.Add(reqId);

            await using var check = CreateContext();
            var req = await check.Requests.SingleAsync(r => r.RequestId == reqId);
            Assert.Equal(cin, req.Cin);

            // Job created and queued
            Assert.True(await check.AutoFetchJobs.AnyAsync(j => j.RequestId == reqId));
            Assert.True(queue.TryRead(out var jobId));
            Assert.True(jobId > 0);

            // Resolution recorded as HumanSelected
            var res = await check.IdentityResolutions.Where(r => r.RequestId == reqId).OrderByDescending(r => r.CreatedUtc).FirstOrDefaultAsync();
            Assert.NotNull(res);
            Assert.Equal(ResolutionMethod.HumanSelected, res.Method);
            Assert.Equal(cin, res.ChosenIdentifier);
        }
    }

    [Fact]
    public async Task AmbiguityQueue_ListsNeedsAttentionRequests_WithRankedCandidates()
    {
        var cin1 = Cin(1);
        var cin2 = Cin(2);
        await SeedMasterAsync(
            (cin1, $"{_token} TRADING PRIVATE LIMITED", "Gujarat", "Ahmedabad"),
            (cin2, $"{_token} TRADERS PRIVATE LIMITED", "Gujarat", "Surat"));

        var (afController, queue, identity, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            // Create a name-only request that lands in NeedsConfirmation / Ambiguous
            var model = new AutoFetchRequestViewModel
            {
                ClientId = 1,
                CompanyName = $"{_token} Trading Pvt Ltd",
                EntityType = EntityType.Company,
                EntityTypeConfirmed = true
            };
            var result = await afController.New(model, CancellationToken.None);
            var reqId = (long)((RedirectToActionResult)result).RouteValues!["id"]!;
            _requests.Add(reqId);

            await using (var recDb = CreateContext())
            {
                var run = await recDb.PipelineRuns.SingleAsync(r => r.RequestId == reqId);
                await Reconciler(recDb).ReconcileAsync(run.PipelineRunId, CancellationToken.None);
            }

            var options = Options.Create(new ReferenceToolOptions { BaseUrl = "https://test", SessionCookie = "abc" });
            var client = new ReferenceToolClient(new HttpClient(new NoNetworkHandler()), options, new ReferenceToolSession(), new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);
            var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
            var (pipeController, _, _, pDb) = CreatePipeline(jobs, queue, identity);
            await using (pDb)
            {
                var actionResult = await pipeController.AmbiguityQueue(reqId, CancellationToken.None);
                var view = Assert.IsType<ViewResult>(actionResult);
                var vm = Assert.IsType<AmbiguityQueueViewModel>(view.Model);

                Assert.Single(vm.Items);
                var item = vm.Items[0];
                Assert.Equal(reqId, item.RequestId);
                Assert.Equal($"{_token} Trading Pvt Ltd", item.InputName);
                Assert.NotEmpty(item.Candidates);
                Assert.Contains(item.Candidates, c => c.Identifier == cin1);
            }
        }
    }

    [Fact]
    public async Task SelectCin_AppliesSelection_QueuesNewJob_AndUnblocksPipeline()
    {
        var chosenCin = Cin(1);
        await SeedMasterAsync((chosenCin, $"{_token} MOTORS PRIVATE LIMITED", "Tamil Nadu", "Chennai"));

        var (afController, queue, identity, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            // Create name-only request (no CIN, no job)
            var model = new AutoFetchRequestViewModel
            {
                ClientId = 1,
                CompanyName = $"{_token} Motors",
                EntityType = EntityType.Company,
                EntityTypeConfirmed = true
            };
            var result = await afController.New(model, CancellationToken.None);
            var reqId = (long)((RedirectToActionResult)result).RouteValues!["id"]!;
            _requests.Add(reqId);

            await using (var recDb = CreateContext())
            {
                var run = await recDb.PipelineRuns.SingleAsync(r => r.RequestId == reqId);
                await Reconciler(recDb).ReconcileAsync(run.PipelineRunId, CancellationToken.None);
            }

            var options = Options.Create(new ReferenceToolOptions { BaseUrl = "https://test", SessionCookie = "abc" });
            var client = new ReferenceToolClient(new HttpClient(new NoNetworkHandler()), options, new ReferenceToolSession(), new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);
            var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
            var (pipeController, pipeQueue, _, pDb) = CreatePipeline(jobs, queue, identity);
            await using (pDb)
            {
                // Human reviewer clicks "Select this CIN"
                var selectResult = await pipeController.SelectCin(reqId, chosenCin, returnUrl: "/Pipeline/AmbiguityQueue", CancellationToken.None);

                Assert.Equal($"Successfully selected {chosenCin} for request.", pipeController.TempData["PipelineOk"]);

                await using var check = CreateContext();
                var req = await check.Requests.SingleAsync(r => r.RequestId == reqId);
                Assert.Equal(chosenCin, req.Cin);
                Assert.Equal(chosenCin, req.AutoFetchCompanyIdentifier);

                // AutoFetchJob was created for this previously job-less request and queued!
                var job = await check.AutoFetchJobs.FirstOrDefaultAsync(j => j.RequestId == reqId);
                Assert.NotNull(job);
                Assert.Equal(chosenCin, job.Cin);
                Assert.True(pipeQueue.TryRead(out var queuedJobId));
                Assert.Equal(job.AutoFetchJobId, queuedJobId);

                // Resolution audit row records HumanSelected
                var latestRes = await check.IdentityResolutions.Where(r => r.RequestId == reqId).OrderByDescending(r => r.CreatedUtc).FirstAsync();
                Assert.Equal(ResolutionMethod.HumanSelected, latestRes.Method);
                Assert.Equal(chosenCin, latestRes.ChosenIdentifier);

                // Pipeline event recorded
                var evt = await check.PipelineEvents.Where(e => e.Action == PipelineEventActions.IdentitySelected).FirstOrDefaultAsync();
                Assert.NotNull(evt);
                Assert.Equal(PipelineStage.Resolve, evt.Stage);
            }
        }
    }

    [Fact]
    public async Task SelectCin_WithEmptyIdentifier_SetsTempDataError()
    {
        var (afController, queue, identity, db) = CreateAutoFetch(autoSelect: false);
        await using (db)
        {
            var options = Options.Create(new ReferenceToolOptions { BaseUrl = "https://test", SessionCookie = "abc" });
            var client = new ReferenceToolClient(new HttpClient(new NoNetworkHandler()), options, new ReferenceToolSession(), new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);
            var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
            var (pipeController, _, _, pDb) = CreatePipeline(jobs, queue, identity);
            await using (pDb)
            {
                await pipeController.SelectCin(12345, "   ", returnUrl: "/Pipeline/AmbiguityQueue", CancellationToken.None);
                Assert.NotNull(pipeController.TempData["PipelineError"]);
            }
        }
    }

    private sealed class StaticOptionsMonitor(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("no network in tests");
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed class FakeEnv(string contentRoot) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Tests";
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
