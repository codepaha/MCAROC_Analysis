using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>Issue #266's verification against the real SQLEXPRESS test database and a fake reference tool:
/// racing executors on one approval spend exactly once; wrong-company and expired approvals are rejected; an
/// existing unlock is adopted for free; the identity check blocks a mismatched spend; a failed paid call is
/// never retried; one approval covers every waiting request. Each test runs on its own far-future fake date (so
/// its daily unlock counter is private) and its own company, and deletes what it created.</summary>
public sealed class CompanyUnlockServiceTests : IAsyncLifetime
{
    private readonly FakeTime _time = new(new DateTimeOffset(2200 + Random.Shared.Next(0, 700), 3, 1, 6, 0, 0, TimeSpan.Zero).AddDays(Random.Shared.Next(0, 300)));
    private readonly string _cin = $"U{Random.Shared.Next(10000, 99999)}KA2021PTC{Random.Shared.Next(100000, 999999)}";
    private string Bid => ReferenceToolClient.ComputeBid(_cin);
    private DateTimeOffset Now => _time.GetUtcNow();

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public async Task DisposeAsync()
    {
        await using var db = CreateContext();
        var requestIds = await db.Requests.Where(r => r.Cin == _cin).Select(r => r.RequestId).ToListAsync();
        await db.AutoFetchJobs.Where(j => j.Cin == _cin).ExecuteDeleteAsync();
        await db.PaidCallAdmissions.Where(a => requestIds.Contains(a.RequestId)).ExecuteDeleteAsync();
        await db.SpendScopes.Where(s => s.ScopeKey == "unlock|" + _cin).ExecuteDeleteAsync();
        await db.UnlockApprovals.Where(a => a.Identifier == _cin).ExecuteDeleteAsync();
        await db.Requests.Where(r => r.Cin == _cin).ExecuteDeleteAsync();
        await db.CompanyReportLifecycles.Where(l => l.Identifier == _cin).ExecuteDeleteAsync();
    }

    private FakeReferenceTool LockedTool() => new(Bid) { AddedAt = null, PreviewCin = _cin, DataAsOf = null, Clock = () => _time.GetUtcNow() };

    private ServiceProvider Services(FakeReferenceTool tool, PipelineOptions? pipeline = null, AutoFetchQueue? queue = null) =>
        tool.BuildServices(_time, queue ?? new AutoFetchQueue(), pipeline);

    private static async Task<T> InScope<T>(ServiceProvider sp, Func<IServiceProvider, Task<T>> body)
    {
        using var scope = sp.CreateScope();
        return await body(scope.ServiceProvider);
    }

    private Task<UnlockResult> ExecuteAsync(ServiceProvider sp, long requestId) =>
        InScope(sp, s => s.GetRequiredService<CompanyUnlockService>().ExecuteAsync(_cin, Bid, requestId, CancellationToken.None));

    private Task<UnlockApproval> ApproveAsync(ServiceProvider sp, long requestId) =>
        InScope(sp, s => s.GetRequiredService<CompanyUnlockService>().ApproveAsync(requestId, "reviewer", "client needs the report", CancellationToken.None));

    /// <summary>The racers' last pre-spend check is staggered and the paid call is slow, so some racers read
    /// the approval as open, then reach the admission after the winner has released the scope and while its
    /// paid call is still running — the window where only the one-row approval consumption stops a second spend.</summary>
    [Fact]
    public async Task Racing_executors_on_one_approval_spend_exactly_one_credit()
    {
        var tool = LockedTool();
        tool.UnlockCheckDelay = () => TimeSpan.FromMilliseconds(Random.Shared.Next(0, 400));
        tool.AddAssetDelay = TimeSpan.FromMilliseconds(800);
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        var approval = await ApproveAsync(sp, requestId);

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { gate.Wait(); return await ExecuteAsync(sp, requestId); })).ToArray();
        gate.Set();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, tool.Count("addAsset"));
        Assert.Single(results, r => r.Outcome == UnlockOutcome.Unlocked);
        await using var db = CreateContext();
        var consumed = await db.UnlockApprovals.AsNoTracking().SingleAsync(a => a.UnlockApprovalId == approval.UnlockApprovalId);
        Assert.NotNull(consumed.ConsumedAdmissionId);
        var admissions = await db.PaidCallAdmissions.AsNoTracking().Where(a => a.Kind == PaidCallKind.ReferenceUnlock && a.RequestId == requestId).ToListAsync();
        var admission = Assert.Single(admissions);
        Assert.Equal(PaidCallAdmissionState.Committed, admission.State);
        Assert.Equal(PaidCallTrigger.Manual, admission.Trigger);
        Assert.Equal(consumed.ConsumedAdmissionId, admission.PaidCallAdmissionId);
    }

    /// <summary>PR #286 review: concurrent approve clicks must not create more than one open approval — a
    /// second, never-expiring approval could otherwise outlive a failed unlock and authorize a later spend.</summary>
    [Fact]
    public async Task Concurrent_approvals_for_one_company_create_exactly_one_open_approval()
    {
        var sp = Services(LockedTool());
        var (requestId, _) = await SeedJobAsync();

        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { gate.Wait(); return await ApproveAsync(sp, requestId); })).ToArray();
        gate.Set();
        var approvals = await Task.WhenAll(tasks);

        Assert.Single(approvals.Select(a => a.UnlockApprovalId).Distinct());
        await using var db = CreateContext();
        Assert.Equal(1, await db.UnlockApprovals.CountAsync(a => a.Identifier == _cin));
    }

    /// <summary>The exact scenario from the review: concurrent approvals, then the paid call fails. No approval
    /// may survive to authorize another spend — a new one has to be given deliberately.</summary>
    [Fact]
    public async Task After_concurrent_approvals_and_a_failed_unlock_nothing_can_authorize_another_spend()
    {
        var tool = LockedTool();
        tool.AddAssetStatus = System.Net.HttpStatusCode.InternalServerError;
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ApproveAsync(sp, requestId)));

        Assert.Equal(UnlockOutcome.Failed, (await ExecuteAsync(sp, requestId)).Outcome);
        Assert.Equal(UnlockOutcome.NoApproval, (await ExecuteAsync(sp, requestId)).Outcome);

        Assert.Equal(1, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.False(await db.UnlockApprovals.AnyAsync(a => a.Identifier == _cin && a.ConsumedAdmissionId == null && a.ExpiresUtc > Now.UtcDateTime));
    }

    /// <summary>PR #286 review: committing the admission releases the scope before the paid call returns, and
    /// manual admissions skip the cooldown — so a second approve + resume while the first <c>addAsset</c> is still
    /// in flight must be fenced out, or it spends a second credit.</summary>
    [Fact]
    public async Task A_second_approval_and_resume_while_the_paid_call_is_in_flight_does_not_spend_again()
    {
        var tool = LockedTool();
        tool.AddAssetDelay = TimeSpan.FromMilliseconds(1500);
        var sp = Services(tool);
        var (requestId, jobId) = await SeedJobAsync();
        await ProcessAsync(sp, jobId);
        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, (await JobAsync(jobId)).Status);

        Task ApproveAndResume() => Task.Run(async () =>
        {
            await ApproveAsync(sp, requestId);
            await InScope(sp, s => s.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(_cin, Bid, CancellationToken.None));
        });

        var first = ApproveAndResume();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (tool.Count("addAsset") == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal(1, tool.Count("addAsset")); // the first paid call is now in flight
        var second = ApproveAndResume();
        await Task.WhenAll(first, second);

        Assert.Equal(1, tool.Count("addAsset"));
        await InScope(sp, s => s.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(_cin, Bid, CancellationToken.None));
        Assert.Equal(1, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.Equal(1, await db.PaidCallAdmissions.CountAsync(a => a.RequestId == requestId && a.Kind == PaidCallKind.ReferenceUnlock));
        Assert.False(await db.UnlockApprovals.AnyAsync(a => a.Identifier == _cin && a.ConsumedAdmissionId == null && a.ExpiresUtc > Now.UtcDateTime));
    }

    [Fact]
    public async Task Without_an_approval_nothing_is_spent()
    {
        var tool = LockedTool();
        var (requestId, _) = await SeedJobAsync();

        var result = await ExecuteAsync(Services(tool), requestId);

        Assert.Equal(UnlockOutcome.NoApproval, result.Outcome);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.False(await db.PaidCallAdmissions.AnyAsync(a => a.RequestId == requestId));
    }

    [Fact]
    public async Task An_approval_for_a_different_company_or_an_expired_one_is_not_used()
    {
        var tool = LockedTool();
        var (requestId, _) = await SeedJobAsync();
        await using (var db = CreateContext())
        {
            db.UnlockApprovals.AddRange(
                new UnlockApproval { Identifier = "U99999KA2021PTC999999", RequestId = requestId, ApprovedBy = "r", ApprovedUtc = Now.UtcDateTime, ExpiresUtc = DateTime.MaxValue },
                new UnlockApproval { Identifier = _cin, RequestId = requestId, ApprovedBy = "r", ApprovedUtc = Now.UtcDateTime.AddDays(-2), ExpiresUtc = Now.UtcDateTime.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        var result = await ExecuteAsync(Services(tool), requestId);

        Assert.Equal(UnlockOutcome.NoApproval, result.Outcome);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var verify = CreateContext();
        Assert.True(await verify.UnlockApprovals.Where(a => a.RequestId == requestId).AllAsync(a => a.ConsumedAdmissionId == null));
        await verify.UnlockApprovals.Where(a => a.Identifier == "U99999KA2021PTC999999" && a.RequestId == requestId).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_company_already_unlocked_by_anyone_is_adopted_for_free_and_open_approvals_are_retired()
    {
        var tool = LockedTool();
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        await ApproveAsync(sp, requestId);
        tool.AddedAt = Now.AddDays(-10); // someone else unlocked it meanwhile

        var result = await ExecuteAsync(sp, requestId);

        Assert.Equal(UnlockOutcome.AlreadyUnlocked, result.Outcome);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.False(await db.UnlockApprovals.AnyAsync(a => a.Identifier == _cin && a.ConsumedAdmissionId == null && a.ExpiresUtc > Now.UtcDateTime),
            "a never-expiring approval must not linger to authorize a surprise spend later");
    }

    [Fact]
    public async Task A_preview_naming_a_different_company_blocks_the_spend_and_keeps_the_approval()
    {
        var tool = LockedTool();
        tool.PreviewCin = "U11111KA2021PTC111111";
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        await ApproveAsync(sp, requestId);

        var result = await ExecuteAsync(sp, requestId);

        Assert.Equal(UnlockOutcome.IdentityMismatch, result.Outcome);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.True(await db.UnlockApprovals.AnyAsync(a => a.Identifier == _cin && a.ConsumedAdmissionId == null));
    }

    [Fact]
    public async Task MCA_maintenance_defers_without_consuming_the_approval()
    {
        var tool = LockedTool();
        tool.UnlockMcaStatus = "UNDER_MAINTENANCE";
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        await ApproveAsync(sp, requestId);

        Assert.Equal(UnlockOutcome.Deferred, (await ExecuteAsync(sp, requestId)).Outcome);
        Assert.Equal(0, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.True(await db.UnlockApprovals.AnyAsync(a => a.Identifier == _cin && a.ConsumedAdmissionId == null));
    }

    [Fact]
    public async Task A_failed_paid_call_is_recorded_as_spent_and_never_retried_without_a_new_approval()
    {
        var tool = LockedTool();
        tool.AddAssetStatus = System.Net.HttpStatusCode.InternalServerError;
        var sp = Services(tool);
        var (requestId, _) = await SeedJobAsync();
        await ApproveAsync(sp, requestId);

        var failed = await ExecuteAsync(sp, requestId);
        Assert.Equal(UnlockOutcome.Failed, failed.Outcome);
        Assert.Contains("may have been spent", failed.Message);

        var again = await ExecuteAsync(sp, requestId);
        Assert.Equal(UnlockOutcome.NoApproval, again.Outcome);
        Assert.Equal(1, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.Equal(PaidCallAdmissionState.Committed,
            (await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == requestId && a.Kind == PaidCallKind.ReferenceUnlock)).State);
    }

    [Fact]
    public async Task Auto_unlock_is_off_by_default_bounded_by_the_daily_cap_when_on()
    {
        var (requestId, _) = await SeedJobAsync();

        var off = await ExecuteAsync(Services(LockedTool()), requestId);
        Assert.Equal(UnlockOutcome.NoApproval, off.Outcome);

        var capZero = new PipelineOptions { AutoUnlock = { Enabled = true } };
        var denied = await ExecuteAsync(Services(LockedTool(), capZero), requestId);
        Assert.Equal(UnlockOutcome.Deferred, denied.Outcome);
        Assert.Contains("limit", denied.Message);

        var tool = LockedTool();
        var capOne = new PipelineOptions { AutoUnlock = { Enabled = true }, Caps = { UnlockPerDay = 1 } };
        var unlocked = await ExecuteAsync(Services(tool, capOne), requestId);
        Assert.Equal(UnlockOutcome.Unlocked, unlocked.Outcome);
        Assert.Equal(1, tool.Count("addAsset"));
        await using var db = CreateContext();
        Assert.Equal(PaidCallTrigger.Auto,
            (await db.PaidCallAdmissions.AsNoTracking().SingleAsync(a => a.RequestId == requestId && a.Kind == PaidCallKind.ReferenceUnlock)).Trigger);
    }

    // ── Through the job and the coordinator ─────────────────────────────────────────────────────────

    [Fact]
    public async Task One_approval_unlocks_once_for_every_request_waiting_on_the_company_and_they_resume()
    {
        var tool = LockedTool();
        var queue = new AutoFetchQueue();
        var sp = Services(tool, queue: queue);
        var (requestA, jobA) = await SeedJobAsync();
        var (_, jobB) = await SeedJobAsync();

        await ProcessAsync(sp, jobA);
        await ProcessAsync(sp, jobB);
        var parked = await JobAsync(jobA);
        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, parked.Status);
        Assert.StartsWith("UNLOCK_APPROVAL_REQUIRED", parked.StatusMessage);
        Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, (await JobAsync(jobB)).Status);
        Assert.Equal(0, tool.Count("addAsset"));

        await ApproveAsync(sp, requestA);
        await InScope(sp, s => s.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(_cin, Bid, CancellationToken.None));

        Assert.Equal(1, tool.Count("addAsset"));
        Assert.Equal(AutoFetchJobStatus.Queued, (await JobAsync(jobA)).Status);
        Assert.Equal(AutoFetchJobStatus.Queued, (await JobAsync(jobB)).Status);
        var requeued = new List<long>();
        while (queue.TryRead(out var id)) requeued.Add(id);
        Assert.Equal(new[] { jobA, jobB }.Order(), requeued.Order());

        // Next pass: the unlock is adopted and (data unknown → stale) a refresh starts; the unlock date then never moves.
        await ProcessAsync(sp, jobA);
        await using var db = CreateContext();
        var lifecycle = await db.CompanyReportLifecycles.AsNoTracking().SingleAsync(l => l.Identifier == _cin);
        Assert.Equal(Now.UtcDateTime, lifecycle.UnlockedUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(CompanyReportLifecycleState.Refreshing, lifecycle.State);
        tool.RefreshPending = false;
        tool.DataAsOf = Now.AddMinutes(1);
        _time.Advance(TimeSpan.FromHours(2));
        await InScope(sp, s => s.GetRequiredService<CompanyGateCoordinator>().ResumeAsync(_cin, Bid, CancellationToken.None));
        var afterRefresh = await db.CompanyReportLifecycles.AsNoTracking().SingleAsync(l => l.Identifier == _cin);
        Assert.Equal(lifecycle.UnlockedUtc, afterRefresh.UnlockedUtc);
    }

    [Fact]
    public async Task A_job_whose_preview_names_a_different_company_fails_without_spending()
    {
        var tool = LockedTool();
        tool.PreviewCin = "U22222KA2021PTC222222";
        var sp = Services(tool);
        var (_, jobId) = await SeedJobAsync();

        await ProcessAsync(sp, jobId);

        var job = await JobAsync(jobId);
        Assert.Equal(AutoFetchJobStatus.Failed, job.Status);
        Assert.StartsWith("IDENTITY_MISMATCH", job.FailureReason);
        Assert.Equal(0, tool.Count("addAsset"));
    }

    [Theory]
    [InlineData(11, false)] // month 11: still unlocked — reused, no approval needed
    [InlineData(13, true)]  // past 12 months: the tool reports it locked again — expired, needs an approval
    public async Task The_unlock_window_is_12_months_from_the_original_unlock(int monthsAgo, bool expectExpired)
    {
        var tool = LockedTool();
        tool.AddedAt = Now.AddMonths(-monthsAgo);
        tool.DataAsOf = Now.AddHours(-1);
        var sp = Services(tool);
        var (_, jobId) = await SeedJobAsync();
        await InScope(sp, s => s.GetRequiredService<CompanyRefreshService>().EvaluateAsync(_cin, Bid, CancellationToken.None)); // adopts the date

        if (expectExpired) tool.AddedAt = null; // what the tool reports once the 12 months have passed
        await ProcessAsync(sp, jobId);

        await using var db = CreateContext();
        var lifecycle = await db.CompanyReportLifecycles.AsNoTracking().SingleAsync(l => l.Identifier == _cin);
        if (expectExpired)
        {
            Assert.Equal(CompanyReportLifecycleState.Expired, lifecycle.State);
            Assert.Equal(AutoFetchJobStatus.WaitingForUnlock, (await JobAsync(jobId)).Status);
        }
        else
        {
            Assert.Equal(CompanyReportLifecycleState.Unlocked, lifecycle.State);
            Assert.Equal(1, tool.Count("publishProbedData")); // went straight on to export
        }
        Assert.Equal(0, tool.Count("addAsset"));
    }

    private async Task ProcessAsync(ServiceProvider sp, long jobId)
    {
        using var scope = sp.CreateScope();
        var s = scope.ServiceProvider;
        var jobs = new AutoFetchJobService(s.GetRequiredService<AppDbContext>(), s.GetRequiredService<ReferenceToolClient>(), FakeReferenceTool.Options(),
            new FileValidationService(new ExcelSheetReader()), null!, null!, new FilingProcessingQueue(), null!, new FakeEnv(Path.GetTempPath()),
            NullLogger<AutoFetchJobService>.Instance, refresh: s.GetRequiredService<CompanyRefreshService>(), unlock: s.GetRequiredService<CompanyUnlockService>());
        await jobs.ProcessAsync(jobId, CancellationToken.None);
    }

    private async Task<(long RequestId, long JobId)> SeedJobAsync()
    {
        await using var db = CreateContext();
        var client = new Client { ClientCode = "CUS" + Guid.NewGuid().ToString("N")[..7], ClientName = "Unlock Test Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Unlock Test Company",
            Cin = _cin, RequestNumber = $"CUS-{Guid.NewGuid():N}", RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
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
        return (request.RequestId, job.AutoFetchJobId);
    }

    private static async Task<AutoFetchJob> JobAsync(long jobId)
    {
        await using var db = CreateContext();
        return await db.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);
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
