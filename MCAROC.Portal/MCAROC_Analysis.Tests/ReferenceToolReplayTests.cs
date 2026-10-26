using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Capture;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>#514: the E2E matrix profiles that the reference tool alone decides, replayed against what the tool really answered
/// (<see cref="FixtureReferenceTool"/>, <c>Fixtures/reference-tool/</c>) rather than replies written from our assumptions. Each run requires that the
/// client asked for nothing the tool was never recorded answering. The runs stop where the export would begin: a workbook is real company data
/// and stays out of the repository, so ingestion onwards is covered by the ingestion tests and the live round. Litigation profiles (6, 7) wait for
/// the litigation service (owner, 2026-10-24) and the large-company registry profile (10) for #515's capture.</summary>
public sealed class ReferenceToolReplayTests : IAsyncLifetime
{
    private const string TcsCin = "L22210MH1995PLC084781";   // its recordings name it, and no other test uses it
    private const string NatureEarthLlpin = "ACE-3794";
    private readonly string _coastalStandIn = $"U{Random.Shared.Next(10000, 99999)}OR1995PLC{Random.Shared.Next(100000, 999999)}"; // Coastal's CIN is shared by many tests
    private readonly List<string> _identifiers = [];

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        _identifiers.AddRange([_coastalStandIn, TcsCin, NatureEarthLlpin]);
        await CleanAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await CleanAsync(db);
    }

    private async Task CleanAsync(AppDbContext db)
    {
        var requestIds = await db.Requests.Where(r => _identifiers.Contains(r.Cin!)).Select(r => r.RequestId).ToListAsync();
        await db.AutoFetchJobs.Where(j => _identifiers.Contains(j.Cin)).ExecuteDeleteAsync();
        await db.PaidCallAdmissions.Where(a => requestIds.Contains(a.RequestId)).ExecuteDeleteAsync();
        await db.SpendScopes.Where(s => _identifiers.Select(i => "unlock|" + i).Contains(s.ScopeKey)).ExecuteDeleteAsync();
        await db.UnlockApprovals.Where(a => _identifiers.Contains(a.Identifier)).ExecuteDeleteAsync();
        await db.Requests.Where(r => requestIds.Contains(r.RequestId)).ExecuteDeleteAsync();
        await db.CompanyReportLifecycles.Where(l => _identifiers.Contains(l.Identifier)).ExecuteDeleteAsync();
    }

    private static ServiceProvider Services(HttpMessageHandler tool, FakeTime time) =>
        FakeReferenceTool.BuildServices(tool, time, new AutoFetchQueue(), extra: services => services.AddScoped(sp => new AutoFetchJobService(
            sp.GetRequiredService<AppDbContext>(), FakeReferenceTool.NewClient(tool), FakeReferenceTool.Options(), new FileValidationService(new ExcelSheetReader()),
            null!, new AnalysisQueue(), new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance,
            refresh: sp.GetRequiredService<CompanyRefreshService>(), unlock: sp.GetRequiredService<CompanyUnlockService>())));

    private static async Task<T> InScope<T>(ServiceProvider sp, Func<IServiceProvider, Task<T>> body)
    {
        using var scope = sp.CreateScope();
        return await body(scope.ServiceProvider);
    }

    private static Task<RefreshGateResult> EvaluateAsync(ServiceProvider sp, string identifier) =>
        InScope(sp, s => s.GetRequiredService<CompanyRefreshService>().EvaluateAsync(identifier, ReferenceToolClient.ComputeBid(identifier), CancellationToken.None));

    private static async Task<AutoFetchJob> ProcessNewJobAsync(ServiceProvider sp, string identifier, EntityType entityType, string companyName = "Replay Test Company")
    {
        long jobId;
        await using (var db = CreateContext())
        {
            var request = new McaRequest
            {
                Client = new Client { ClientCode = "RPL" + Guid.NewGuid().ToString("N")[..7], ClientName = "Replay Test Client", CreatedDate = DateTime.UtcNow },
                EntityType = entityType, CompanyName = companyName, Cin = identifier,
                RequestNumber = $"RPL-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
            };
            db.Requests.Add(request);
            await db.SaveChangesAsync();
            var job = new AutoFetchJob
            {
                RequestId = request.RequestId, Cin = identifier, Bid = ReferenceToolClient.ComputeBid(identifier), Status = AutoFetchJobStatus.Queued,
                IncludeFilings = false, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
            };
            db.AutoFetchJobs.Add(job);
            await db.SaveChangesAsync();
            jobId = job.AutoFetchJobId;
        }
        await InScope(sp, async s => { await s.GetRequiredService<AutoFetchJobService>().ProcessAsync(jobId, CancellationToken.None); return 0; });
        await using var read = CreateContext();
        return await read.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
    }

    private static async Task<CompanyReportLifecycle> LifecycleAsync(string identifier)
    {
        await using var db = CreateContext();
        return await db.CompanyReportLifecycles.AsNoTracking().SingleAsync(l => l.Identifier == identifier);
    }

    private static void AssertAskedOnlyForRecordedCalls(FixtureReferenceTool tool) =>
        Assert.True(tool.Unanswered.IsEmpty, $"Asked {tool.Scenario} for calls it was never recorded answering: {string.Join(", ", tool.Unanswered)}");

    [Theory]
    [InlineData("unlocked-stale-coastal-20260922", "U45203OR1995PLC003982")]
    [InlineData("locked-unlock-tcs-20260922", TcsCin)]
    [InlineData("never-probed-llp-20261016", NatureEarthLlpin)]
    [InlineData("registry-paging-godrej-20261024", "L74120MH1985PLC035308")]
    public void Each_recording_was_made_for_the_company_its_identifier_hashes_to(string scenario, string identifier) =>
        Assert.Equal(ReferenceToolClient.ComputeBid(identifier), new FixtureReferenceTool(scenario).RecordedBid);

    [Fact]
    public void Every_recording_says_where_it_came_from_and_holds_no_secret()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Fixtures", "reference-tool");
        foreach (var file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            Assert.False(string.IsNullOrWhiteSpace(System.Text.Json.Nodes.JsonNode.Parse(text)!["source"]?.GetValue<string>()), $"{file} has no source");
            Assert.Empty(CaptureRedactor.FindLeaks(text));
        }
    }

    /// <summary>Profile 1: unlocked, data under 24 hours old. Exported as it is: no MCA check, no refresh.</summary>
    [Fact]
    public async Task Profile_1_data_under_a_day_old_is_used_as_it_is()
    {
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922");
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 8, 21, 6, 0, 0, TimeSpan.Zero))); // the data is from 2026-08-20 20:22 IST

        var gate = await EvaluateAsync(sp, _coastalStandIn);

        Assert.Equal(RefreshGateKind.Ready, gate.Kind);
        Assert.Equal(new DateTime(2026, 8, 20, 14, 52, 16, DateTimeKind.Utc), gate.DataAsOfUtc);         // read from the real reply's "downloaded"
        Assert.Equal(new DateTime(2026, 8, 20, 10, 28, 16, DateTimeKind.Utc), (await LifecycleAsync(_coastalStandIn)).UnlockedUtc); // its real unlock time
        Assert.Equal(0, tool.Count("getUpgradeStatusForCompanies"));
        Assert.Equal(0, tool.Count("requestProbeDataUpdate") + tool.Count("requestProbeData"));
        AssertAskedOnlyForRecordedCalls(tool);
    }

    /// <summary>Profile 2: unlocked, data a month old. One refresh is requested the way the tool's own UI does (update, never the first-probe action),
    /// after a companies MCA check, and the gate waits.</summary>
    [Fact]
    public async Task Profile_2_stale_data_asks_for_one_update_and_waits()
    {
        var tool = new FixtureReferenceTool("unlocked-stale-coastal-20260922");
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 9, 22, 4, 6, 0, TimeSpan.Zero)));

        var gate = await EvaluateAsync(sp, _coastalStandIn);

        Assert.Equal(RefreshGateKind.Waiting, gate.Kind);
        Assert.Equal(1, tool.Count("getUpgradeStatusForCompanies"));
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));
        Assert.Equal(0, tool.Count("requestProbeData"));
        Assert.Equal(CompanyReportLifecycleState.Refreshing, (await LifecycleAsync(_coastalStandIn)).State);
        AssertAskedOnlyForRecordedCalls(tool);
    }

    /// <summary>Profiles 3, 4 and 8 together, as on 2026-10-16: an LLP the tool had never probed, during an MCA ALERT. The LLP status is asked, the ALERT
    /// proceeds, the first probe is requested with requestProbeData (the update action 404s here), and the job waits for it rather than exporting
    /// nothing (export-while-refreshing does not apply to a company with no data at all).</summary>
    [Fact]
    public async Task Profiles_3_4_8_a_never_probed_llp_during_an_alert_gets_its_first_probe_and_waits_for_it()
    {
        var tool = new FixtureReferenceTool("never-probed-llp-20261016");
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 10, 16, 13, 50, 0, TimeSpan.Zero)));

        var job = await ProcessNewJobAsync(sp, NatureEarthLlpin, EntityType.LLP);

        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, job.Status);
        Assert.Equal(1, tool.Count("getUpgradeStatusForLLPs"));
        Assert.Equal(0, tool.Count("getUpgradeStatusForCompanies"));
        Assert.Equal(1, tool.Count("requestProbeData"));
        Assert.Equal(0, tool.Count("requestProbeDataUpdate"));
        Assert.Equal(0, tool.Count("publishProbedData"));
        AssertAskedOnlyForRecordedCalls(tool);
    }

    /// <summary>Profile 8, the other half: real maintenance (not an ALERT) holds the refresh off.</summary>
    [Fact]
    public async Task Profile_8_mca_maintenance_defers_the_probe()
    {
        var tool = new FixtureReferenceTool("never-probed-llp-20261016").With("getUpgradeStatusForLLPs", 200, new { mca_status = "UNDER_MAINTENANCE" });
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 10, 16, 13, 50, 0, TimeSpan.Zero)));

        var gate = await EvaluateAsync(sp, NatureEarthLlpin);

        Assert.Equal(RefreshGateKind.Deferred, gate.Kind);
        Assert.Equal(0, tool.Count("requestProbeData") + tool.Count("requestProbeDataUpdate"));
    }

    /// <summary>Profile 5: a locked company. The job parks for an approval having spent nothing; the approved unlock spends exactly one credit (the real
    /// reply to addAsset) and is confirmed by the tool's asset status afterwards.</summary>
    [Fact]
    public async Task Profile_5_a_locked_company_waits_for_approval_then_one_credit_unlocks_it()
    {
        var tool = new FixtureReferenceTool("locked-unlock-tcs-20260922");
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 9, 22, 4, 10, 0, TimeSpan.Zero)));

        var job = await ProcessNewJobAsync(sp, TcsCin, EntityType.Company);
        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, job.Status);
        Assert.Equal(0, tool.Count("addAsset"));

        await InScope(sp, s => s.GetRequiredService<CompanyUnlockService>().ApproveAsync(job.RequestId, "reviewer", "matrix profile 5", CancellationToken.None));
        var result = await InScope(sp, s => s.GetRequiredService<CompanyUnlockService>().ExecuteAsync(TcsCin, ReferenceToolClient.ComputeBid(TcsCin), job.RequestId, CancellationToken.None));

        Assert.Equal(UnlockOutcome.Unlocked, result.Outcome);
        Assert.Equal(1, tool.Count("addAsset"));
        Assert.Equal(2, tool.Count("getCompanyPreview")); // once before parking, once right before the spend
        // Confirmed from the tool's asset status after the spend (the recorded unlock time), not from addAsset's reply.
        Assert.Contains(Ist.Format(new DateTimeOffset(2026, 9, 22, 9, 41, 59, TimeSpan.FromHours(5.5))), result.Message);
        AssertAskedOnlyForRecordedCalls(tool);
    }

    /// <summary>The #514 round: a request made by its CIN alone is named by its CIN until the workbook comes, and the tool's name search does not match a
    /// CIN; so a locked company's unlock alert asked to spend a credit on "U45403HR2025PTC131813" with no name. The free preview names it before the
    /// job parks, and the request (and the alert, which shows the request's name) says whose unlock it is.</summary>
    [Fact]
    public async Task A_locked_company_requested_by_its_CIN_is_named_from_the_preview_before_it_waits_for_approval()
    {
        var tool = new FixtureReferenceTool("locked-unlock-tcs-20260922");
        var sp = Services(tool, new FakeTime(new DateTimeOffset(2026, 9, 22, 4, 10, 0, TimeSpan.Zero)));

        var job = await ProcessNewJobAsync(sp, TcsCin, EntityType.Company, companyName: TcsCin);

        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, job.Status);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var read = CreateContext();
        Assert.Equal("TATA CONSULTANCY SERVICES LIMITED", await read.Requests.Where(r => r.RequestId == job.RequestId).Select(r => r.CompanyName).SingleAsync());
    }

    /// <summary>Review of #543: a name search that fails on the network (a dropped connection, a timeout) still falls back to the preview.</summary>
    [Fact]
    public async Task A_name_search_that_fails_on_the_network_still_names_the_request_from_the_preview()
    {
        var tool = new FixtureReferenceTool("locked-unlock-tcs-20260922");
        var sp = Services(new SearchFailsOnTheNetwork(tool), new FakeTime(new DateTimeOffset(2026, 9, 22, 4, 10, 0, TimeSpan.Zero)));

        var job = await ProcessNewJobAsync(sp, TcsCin, EntityType.Company, companyName: TcsCin);

        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, job.Status);
        await using var read = CreateContext();
        Assert.Equal("TATA CONSULTANCY SERVICES LIMITED", await read.Requests.Where(r => r.RequestId == job.RequestId).Select(r => r.CompanyName).SingleAsync());
    }

    /// <summary>The recorded scenario, except that the company-name search never gets an answer.</summary>
    private sealed class SearchFailsOnTheNetwork(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.AbsolutePath.Contains("/search/", StringComparison.Ordinal)
                ? throw new HttpRequestException("The connection was reset.")
                : base.SendAsync(request, ct);
    }

    /// <summary>Profile 10 (#515): a large company with 1,354 documents across 4 sections.
    /// Replays the live capture of GODREJ PROPERTIES LIMITED; pages every section and retrieves all 1,354 documents.</summary>
    [Fact]
    public async Task Profile_10_large_company_registry_pages_all_documents()
    {
        var tool = new FixtureReferenceTool("registry-paging-godrej-20261024");
        var client = FakeReferenceTool.NewClient(tool);

        var registry = await client.GetReferenceDocumentsAsync(tool.RecordedBid!, CancellationToken.None);

        Assert.Equal(1354, registry.TotalCount);
        Assert.Equal(1354, registry.ListedCount);
        Assert.Equal(291, registry.Sections.Single(s => s.Key == "annual").Documents.Count);
        Assert.Equal(78, registry.Sections.Single(s => s.Key == "charge").Documents.Count);
        Assert.Equal(181, registry.Sections.Single(s => s.Key == "incorporation").Documents.Count);
        Assert.Equal(804, registry.Sections.Single(s => s.Key == "scanned").Documents.Count);
        Assert.Equal(1, tool.Count("referenceDocs"));
        Assert.Equal(2, tool.Count("getRefDocs"));
        Assert.Equal(1, tool.Count("getIncorporationDocs"));
        Assert.Equal(8, tool.Count("getScannedDocs"));
        AssertAskedOnlyForRecordedCalls(tool);
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private readonly DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
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
