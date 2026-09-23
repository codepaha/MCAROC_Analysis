using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using static MCAROC_Analysis.Tests.TestHelpers;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #229's acceptance criteria against the real SQLEXPRESS test database and a fake reference
/// tool: locked, fresh reuse, stale refresh, concurrent start, restart recovery, provider timeout, and
/// no-stale-download ordering. Each test uses its own company identifier.</summary>
public sealed class CompanyRefreshServiceTests : IAsyncLifetime
{
    private readonly FakeTime _time = new(DateTimeOffset.UtcNow);
    private readonly string _cin = $"U{Random.Shared.Next(10000, 99999)}MH2020PTC{Random.Shared.Next(100000, 999999)}";
    private string Bid => ReferenceToolClient.ComputeBid(_cin);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    /// <summary>Parked jobs left behind would be polled by every later worker test sharing this database.</summary>
    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        await db.AutoFetchJobs.Where(j => j.Cin == _cin).ExecuteDeleteAsync();
        await db.Requests.Where(r => r.Cin == _cin).ExecuteDeleteAsync();
        await db.CompanyReportLifecycles.Where(l => l.Identifier == _cin).ExecuteDeleteAsync();
    }

    private DateTimeOffset Now => _time.GetUtcNow();

    private async Task<RefreshGateResult> EvaluateAsync(FakeReferenceTool tool)
    {
        await using var db = CreateContext();
        return await new CompanyRefreshService(db, tool.NewClient(), FakeReferenceTool.Options(), _time, NullLogger<CompanyRefreshService>.Instance)
            .EvaluateAsync(_cin, Bid, CancellationToken.None);
    }

    private async Task<CompanyReportLifecycle> LifecycleAsync()
    {
        await using var db = CreateContext();
        return await db.CompanyReportLifecycles.AsNoTracking().SingleAsync(l => l.Identifier == _cin);
    }

    [Fact]
    public async Task A_locked_company_reports_locked_and_never_requests_a_refresh()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = null };

        var gate = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Locked, gate.Kind);
        Assert.Equal(CompanyReportLifecycleState.Locked, (await LifecycleAsync()).State);
        Assert.Equal(0, tool.Count("requestProbeDataUpdate"));
        Assert.Equal(0, tool.Count("getDataStatus"));
    }

    [Fact]
    public async Task A_company_whose_12_month_unlock_lapsed_reports_expired()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-13), DataAsOf = Now.AddHours(-1) };
        Assert.Equal(RefreshGateKind.Ready, (await EvaluateAsync(tool)).Kind); // adopts the old unlock date

        tool.AddedAt = null; // the tool now reports it locked again
        var gate = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Locked, gate.Kind);
        Assert.Contains("expired", gate.Message);
        Assert.Equal(CompanyReportLifecycleState.Expired, (await LifecycleAsync()).State);
    }

    [Fact]
    public async Task Data_under_24_hours_old_is_reused_without_a_refresh_and_the_unlock_date_is_adopted()
    {
        var addedAt = Now.AddMonths(-2);
        var tool = new FakeReferenceTool(Bid) { AddedAt = addedAt, DataAsOf = Now.AddHours(-3) };

        var gate = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Ready, gate.Kind);
        Assert.Equal(0, tool.Count("requestProbeDataUpdate"));
        var l = await LifecycleAsync();
        Assert.Equal(CompanyReportLifecycleState.Unlocked, l.State);
        Assert.Equal(addedAt.UtcDateTime, l.UnlockedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.NotNull(l.LastRefreshCompletedUtc);
    }

    [Fact]
    public async Task Stale_data_starts_exactly_one_refresh_and_completes_only_once_the_tool_has_newer_data()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };

        var started = await EvaluateAsync(tool);
        Assert.Equal(RefreshGateKind.Waiting, started.Kind);
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));
        var pending = await LifecycleAsync();
        Assert.Equal(CompanyReportLifecycleState.Refreshing, pending.State);
        Assert.NotNull(pending.ActiveRefreshId);
        Assert.NotNull(pending.RefreshDeadlineUtc);

        // Still running at the tool: keep waiting, no second request.
        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(RefreshGateKind.Waiting, (await EvaluateAsync(tool)).Kind);
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));

        // Done, with data newer than the request.
        tool.RefreshPending = false;
        tool.DataAsOf = Now.AddMinutes(-5);
        var done = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Ready, done.Kind);
        var l = await LifecycleAsync();
        Assert.Equal(CompanyReportLifecycleState.Unlocked, l.State);
        Assert.Null(l.ActiveRefreshId);
        Assert.Equal(tool.DataAsOf!.Value.UtcDateTime, l.LastRefreshCompletedUtc!.Value, TimeSpan.FromSeconds(1));
    }

    /// <summary>PR #285 review: data newer than the request is not enough — a refresh that lands more than 24
    /// hours after it was requested can carry data that is itself already stale. It must not authorize an
    /// export; a new refresh is started instead.</summary>
    [Fact]
    public async Task A_refresh_that_lands_with_data_already_over_24_hours_old_is_not_ready_and_starts_a_new_refresh()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };
        var requested = Now;
        Assert.Equal(RefreshGateKind.Waiting, (await EvaluateAsync(tool)).Kind);
        var firstClaim = (await LifecycleAsync()).ActiveRefreshId;

        _time.Advance(TimeSpan.FromHours(30)); // inside the 36h deadline
        tool.RefreshPending = false;
        tool.DataAsOf = requested.AddHours(1); // newer than the request, but 29 hours old now

        var gate = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Waiting, gate.Kind);
        Assert.Equal(2, tool.Count("requestProbeDataUpdate"));
        var l = await LifecycleAsync();
        Assert.Equal(CompanyReportLifecycleState.Refreshing, l.State);
        Assert.NotNull(l.ActiveRefreshId);
        Assert.NotEqual(firstClaim, l.ActiveRefreshId);
        Assert.Equal(tool.DataAsOf!.Value.UtcDateTime, l.LastRefreshCompletedUtc!.Value, TimeSpan.FromSeconds(1)); // the landing is still recorded
    }

    [Fact]
    public async Task A_parked_job_whose_refresh_lands_with_stale_data_does_not_export()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };
        var requested = Now;
        var jobId = await SeedJobAsync();
        await ProcessAsync(tool, jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, (await JobAsync(jobId)).Status);

        _time.Advance(TimeSpan.FromHours(30));
        tool.RefreshPending = false;
        tool.DataAsOf = requested.AddHours(1);
        await SetStatusAsync(jobId, AutoFetchJobStatus.Queued);
        await ProcessAsync(tool, jobId);

        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, (await JobAsync(jobId)).Status);
        Assert.Equal(0, tool.Count("publishProbedData"));
    }

    [Fact]
    public async Task Concurrent_starts_for_a_stale_company_trigger_one_refresh_and_the_rest_join_it()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { gate.Wait(); return await EvaluateAsync(tool); })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(RefreshGateKind.Waiting, r.Kind));
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));
    }

    [Fact]
    public async Task A_refresh_claimed_but_never_sent_is_re_sent_rather_than_treated_as_done()
    {
        // Simulates a crash between claiming the refresh and calling the tool: the claim is in the database,
        // the tool has nothing pending and still holds the old data.
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3), OnRefreshRequested = _ => { } };
        Assert.Equal(RefreshGateKind.Waiting, (await EvaluateAsync(tool)).Kind); // claims; the request is a no-op

        _time.Advance(TimeSpan.FromMinutes(10));
        var poll = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Waiting, poll.Kind);
        Assert.Equal(2, tool.Count("requestProbeDataUpdate"));
        Assert.NotNull((await LifecycleAsync()).ActiveRefreshId);
    }

    [Fact]
    public async Task A_refresh_past_its_deadline_times_out_and_a_retry_starts_a_new_one()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };
        await EvaluateAsync(tool);

        _time.Advance(TimeSpan.FromHours(37));
        var timedOut = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.TimedOut, timedOut.Kind);
        Assert.StartsWith("REFRESH_TIMEOUT", timedOut.Message);
        var failed = await LifecycleAsync();
        Assert.Equal(CompanyReportLifecycleState.RefreshFailed, failed.State);
        Assert.Null(failed.ActiveRefreshId);

        tool.RefreshPending = false; // the tool has since given up too
        var retry = await EvaluateAsync(tool);
        Assert.Equal(RefreshGateKind.Waiting, retry.Kind);
        Assert.Equal(2, tool.Count("requestProbeDataUpdate"));
    }

    [Fact]
    public async Task MCA_maintenance_defers_without_claiming_a_refresh()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3), McaStatus = "UNDER_MAINTENANCE" };

        var gate = await EvaluateAsync(tool);

        Assert.Equal(RefreshGateKind.Deferred, gate.Kind);
        Assert.Equal(0, tool.Count("requestProbeDataUpdate"));
        Assert.Null((await LifecycleAsync()).ActiveRefreshId);
    }

    [Fact]
    public async Task A_failed_refresh_request_gives_the_claim_back()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3), RefreshRequestStatus = System.Net.HttpStatusCode.ServiceUnavailable };

        await Assert.ThrowsAsync<ReferenceToolException>(() => EvaluateAsync(tool));

        var l = await LifecycleAsync();
        Assert.Null(l.ActiveRefreshId);
        Assert.Equal(CompanyReportLifecycleState.Unlocked, l.State);
    }

    // ── Through the auto-fetch job ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_locked_company_fails_the_job_without_any_export()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = null };
        var jobId = await SeedJobAsync();

        await ProcessAsync(tool, jobId);

        var job = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.Failed, job.Status);
        Assert.Contains("not unlocked", job.FailureReason);
        Assert.Equal(0, tool.Count("publishProbedData"));
    }

    [Fact]
    public async Task A_stale_company_parks_the_job_and_exports_only_after_the_refresh_completes()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };
        var jobId = await SeedJobAsync();

        await ProcessAsync(tool, jobId);
        var parked = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, parked.Status);
        Assert.Equal(0, tool.Count("publishProbedData"));

        // Restart while parked: startup recovery re-queues the job; it re-joins the same refresh.
        await SetStatusAsync(jobId, AutoFetchJobStatus.Queued);
        await ProcessAsync(tool, jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, (await JobAsync(jobId)).Status);
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));

        // A poll while the tool is still busy leaves it parked.
        var queue = new AutoFetchQueue();
        Assert.Equal(0, await NewWorker(tool, queue).PollOnceAsync(CancellationToken.None));

        // The refresh lands; a fresh worker (as after a restart) re-queues the job, which then exports.
        tool.RefreshPending = false;
        tool.DataAsOf = Now.AddMinutes(1);
        Assert.Equal(1, await NewWorker(tool, queue).PollOnceAsync(CancellationToken.None));
        Assert.True(queue.TryRead(out var requeued));
        Assert.Equal(jobId, requeued);
        await ProcessAsync(tool, jobId);

        var calls = tool.Calls.ToList();
        var export = calls.IndexOf("publishProbedData");
        Assert.True(export >= 0, "the job should have exported once the refresh completed");
        var lastCompletedPoll = calls.FindLastIndex(export, c => c == "getDataEntryRequestStatus");
        Assert.True(lastCompletedPoll >= 0 && lastCompletedPoll < export);
        Assert.Equal(1, tool.Count("publishProbedData"));
    }

    [Fact]
    public async Task Recheck_two_workers_and_restart_rejoin_one_refresh_without_old_checkpoints()
    {
        var tool = new FakeReferenceTool(Bid) { AddedAt = Now.AddMonths(-2), DataAsOf = Now.AddDays(-3) };
        var jobId = await SeedJobAsync();
        long requestId;
        long oldRunId;
        await using (var db = CreateContext())
        {
            var job = await db.AutoFetchJobs.SingleAsync(j => j.AutoFetchJobId == jobId);
            requestId = job.RequestId;
            var run = new IngestionRun { RequestId = requestId, RunNumber = 1, StartedDate = DateTime.UtcNow,
                CompletedDate = DateTime.UtcNow, Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(run);
            await db.SaveChangesAsync();
            oldRunId = run.IngestionRunId;
            var request = await db.Requests.SingleAsync(r => r.RequestId == requestId);
            request.LatestCompletedIngestionRunId = oldRunId;
            request.RequestStatus = RequestStatus.AnalysisCompleted;
            job.Status = AutoFetchJobStatus.Completed;
            job.RocDocumentId = 987654;
            job.IngestionRunId = oldRunId;
            await db.SaveChangesAsync();
        }

        await using (var db = CreateContext())
        {
            var jobs = new AutoFetchJobService(db, tool.NewClient(), FakeReferenceTool.Options(),
                new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(),
                null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance);
            Assert.NotNull(await jobs.TryQueueRecheckAsync(requestId, Guid.NewGuid().ToString("N"), CancellationToken.None));
        }
        Assert.Null((await JobAsync(jobId)).RocDocumentId);
        Assert.Null((await JobAsync(jobId)).IngestionRunId);

        using (var start = new Barrier(3))
        {
            async Task RunWorkerAsync()
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(20)));
                await ProcessAsync(tool, jobId);
            }
            var first = Task.Run(RunWorkerAsync);
            var second = Task.Run(RunWorkerAsync);
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(20)));
            await Task.WhenAll(first, second);
        }
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, (await JobAsync(jobId)).Status);
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));
        Assert.Equal(0, tool.Count("publishProbedData"));

        // Simulate a new application instance: recovery moves the parked job to Queued without
        // restoring the previous attempt's checkpoints, then processing joins the same refresh.
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(TestDatabase.ConnectionString));
        using var provider = services.BuildServiceProvider();
        var queue = new AutoFetchQueue();
        var worker = new AutoFetchWorker(provider.GetRequiredService<IServiceScopeFactory>(), queue,
            FakeReferenceTool.Options(), NullLogger<AutoFetchWorker>.Instance);
        await worker.RecoverAsync(CancellationToken.None, requestId);
        Assert.True(queue.TryRead(out var recoveredId));
        Assert.Equal(jobId, recoveredId);
        Assert.Equal(AutoFetchJobStatus.Queued, (await JobAsync(jobId)).Status);
        await ProcessAsync(tool, recoveredId);
        var recovered = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForRefresh, recovered.Status);
        Assert.Null(recovered.RocDocumentId);
        Assert.Null(recovered.IngestionRunId);
        Assert.Equal(1, tool.Count("requestProbeDataUpdate"));
        Assert.Equal(0, tool.Count("publishProbedData"));
        await using var verify = CreateContext();
        Assert.Equal(oldRunId, (await verify.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId)).LatestCompletedIngestionRunId);
    }

    [Fact]
    public async Task Restart_after_ingestion_commit_uses_the_atomic_job_checkpoint_and_does_not_ingest_again()
    {
        var jobId = await SeedJobAsync();
        long requestId;
        long oldRunId;
        long newRunId;
        await using (var db = CreateContext())
        {
            var job = await db.AutoFetchJobs.SingleAsync(j => j.AutoFetchJobId == jobId);
            requestId = job.RequestId;
            var request = await db.Requests.SingleAsync(r => r.RequestId == requestId);
            var oldRun = new IngestionRun { RequestId = requestId, RunNumber = 1, StartedDate = DateTime.UtcNow.AddDays(-1),
                CompletedDate = DateTime.UtcNow.AddDays(-1), Status = IngestionRunStatus.CompletedClean };
            db.IngestionRuns.Add(oldRun);
            await db.SaveChangesAsync();
            oldRunId = oldRun.IngestionRunId;
            request.LatestCompletedIngestionRunId = oldRunId;
            request.RequestStatus = RequestStatus.DocumentsUploaded;
            var roc = new RequestDocument
            {
                RequestId = requestId, DocumentType = DocumentType.McaRocReport, OriginalFileName = "recheck.xls",
                StoredFileName = "recheck.xls", StoragePath = $@"C:\fake\{Guid.NewGuid():N}.xls",
                FileHash = "recheck", UploadedDate = DateTime.UtcNow
            };
            db.RequestDocuments.Add(roc);
            await db.SaveChangesAsync();
            job.Status = AutoFetchJobStatus.Ingesting;
            job.RocDocumentId = roc.DocumentId;
            await db.SaveChangesAsync();

            var reader = new FakeExcelSheetReader(new Dictionary<string, IReadOnlyList<SheetData>>
            {
                [roc.StoragePath] = [Sheet("About the Company",
                    Row("Printed at", "23 Sep, 2026 10:00 Hours"),
                    Row("Legal Name", "REFRESH TEST COMPANY"), Row("CIN", _cin),
                    Row("PAN", "AAAAA0000A"), Row("Company Status", "Active")),
                    Sheet("Directors",
                        Row("NAME", "DIN", "PRESENT DESIGNATION", "PRESENT DESIGNATION APPOINTMENT DATE", "ORIGINAL APPOINTMENT DATE", "DATE OF CESSATION", "FLAGS"),
                        Row("TEST DIRECTOR", 12345678.0, "Director", "1 Jan, 2020", "1 Jan, 2020", "-", "-"))]
            });
            var run = await new IngestionOrchestrator(db, reader, NullLogger<IngestionOrchestrator>.Instance)
                .RunAsync(requestId, roc.DocumentId, chargeDocumentId: null, autoFetchJobId: jobId);
            Assert.NotEqual(IngestionRunStatus.Failed, run.Status);
            newRunId = run.IngestionRunId;
            // Stop here: the caller has not promoted source documents, queued analysis, or saved a
            // separate checkpoint. This is the crash point that previously created run N+2.
        }

        await using (var verify = CreateContext())
        {
            Assert.Equal(newRunId, (await verify.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId)).IngestionRunId);
            Assert.Equal(newRunId, (await verify.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId)).LatestCompletedIngestionRunId);
        }

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(TestDatabase.ConnectionString));
        using var provider = services.BuildServiceProvider();
        var queue = new AutoFetchQueue();
        await new AutoFetchWorker(provider.GetRequiredService<IServiceScopeFactory>(), queue,
            FakeReferenceTool.Options(), NullLogger<AutoFetchWorker>.Instance)
            .RecoverAsync(CancellationToken.None, requestId);
        Assert.True(queue.TryRead(out var recoveredId));
        Assert.Equal(jobId, recoveredId);
        var analysisQueue = new AnalysisQueue();
        await ProcessAsync(new FakeReferenceTool(Bid), recoveredId, analysisQueue);
        using var dequeueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var queued = analysisQueue.ReadAllAsync(dequeueTimeout.Token).GetAsyncEnumerator();
        Assert.True(await queued.MoveNextAsync());
        Assert.Equal(requestId, queued.Current);

        await using var final = CreateContext();
        Assert.Equal(2, await final.IngestionRuns.CountAsync(r => r.RequestId == requestId));
        Assert.Equal(AutoFetchJobStatus.Completed, (await final.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId)).Status);
        Assert.Equal(newRunId, (await final.Requests.AsNoTracking().SingleAsync(r => r.RequestId == requestId)).LatestCompletedIngestionRunId);
        Assert.Equal(IngestionRunStatus.CompletedClean, (await final.IngestionRuns.AsNoTracking().SingleAsync(r => r.IngestionRunId == oldRunId)).Status);
    }

    private async Task ProcessAsync(FakeReferenceTool tool, long jobId, AnalysisQueue? analysisQueue = null)
    {
        await using var db = CreateContext();
        var client = tool.NewClient();
        var refresh = new CompanyRefreshService(db, client, FakeReferenceTool.Options(), _time, NullLogger<CompanyRefreshService>.Instance);
        var jobs = new AutoFetchJobService(db, client, FakeReferenceTool.Options(), new FileValidationService(new ExcelSheetReader()),
            null!, analysisQueue ?? new AnalysisQueue(), new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()), NullLogger<AutoFetchJobService>.Instance,
            refresh: refresh);
        await jobs.ProcessAsync(jobId, CancellationToken.None);
    }

    private CompanyRefreshWorker NewWorker(FakeReferenceTool tool, AutoFetchQueue queue)
    {
        var provider = tool.BuildServices(_time, queue);
        return new CompanyRefreshWorker(provider.GetRequiredService<IServiceScopeFactory>(), FakeReferenceTool.Options(), NullLogger<CompanyRefreshWorker>.Instance);
    }

    private async Task<long> SeedJobAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "CRS" + Guid.NewGuid().ToString("N")[..7], ClientName = "Refresh Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Refresh Test Company",
            Cin = _cin, RequestNumber = $"CRS-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        var job = new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = _cin, Bid = Bid, Status = AutoFetchJobStatus.Queued,
            IncludeFilings = false, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.AutoFetchJobId;
    }

    private static async Task<AutoFetchJob> JobAsync(long jobId)
    {
        await using var db = CreateContext();
        return await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
    }

    private static async Task SetStatusAsync(long jobId, AutoFetchJobStatus status)
    {
        await using var db = CreateContext();
        await db.AutoFetchJobs.Where(j => j.AutoFetchJobId == jobId).ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, status));
    }

    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
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
