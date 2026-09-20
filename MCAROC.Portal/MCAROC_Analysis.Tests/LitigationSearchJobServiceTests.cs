using System.Net;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>End-to-end coverage of LitigationSearchJobService.ProcessAsync against a real test database and
/// a stub HTTP handler standing in for BPR — the same "real service, stubbed HTTP" shape as
/// AutoFetchAggregateCapConcurrencyTests. Poll interval/timeout are configured tiny so these tests run in
/// real time without a fake clock.</summary>
public class LitigationSearchJobServiceTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static async Task<McaRequest> SeedRequestAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "LITJ" + Guid.NewGuid().ToString("N")[..6], ClientName = "Litigation Job Test Co", CreatedDate = DateTime.UtcNow };
        db.Clients.Add(client);
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Litigation Job Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"LITJ-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.DataExtracted, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static readonly BprLitigationOptions DefaultOptions = new()
    {
        BaseUrl = "https://bpr.test/", Id = "app", SecretKey = "secret",
        PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 2
    };

    /// <summary>Builds a service bound to <paramref name="db"/>, sharing <paramref name="handler"/> across
    /// calls if one is passed in (so a test can create the job on one context/service and process it on a
    /// separate one — see the note on <see cref="ProcessAsync_exhausts_attempts_and_ends_Failed_when_authentication_always_fails"/>
    /// below for why that separation matters, not just for realism).</summary>
    private static (LitigationSearchJobService Service, StubHandler Handler) NewService(
        AppDbContext db, BprLitigationOptions? options = null, StubHandler? handler = null)
    {
        var opts = options ?? DefaultOptions;
        handler ??= new StubHandler();
        var client = new BprLitigationClient(
            new HttpClient(handler) { BaseAddress = new Uri(opts.BaseUrl) }, Options.Create(opts), NullLogger<BprLitigationClient>.Instance);
        var service = new LitigationSearchJobService(
            db, client, new LitigationSearchQueue(), Options.Create(opts), NullLogger<LitigationSearchJobService>.Instance);
        return (service, handler);
    }

    private static void StubHappyPath(StubHandler handler, string reportJson = """{"cases":[]}""")
    {
        handler.OnPath("sec/authenticate", _ => JsonResponse("""{"jwt":"token-1"}"""));
        handler.OnPath("bprjob/register", _ => JsonResponse("""{"job_id":"vendor-job-1"}"""));
        handler.OnPath("report/job/", _ => JsonResponse(reportJson));
    }

    private static HttpResponseMessage JsonResponse(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CreateOrResetJobAsync_persists_the_approved_keyword_plan_and_starts_Pending()
    {
        await using var db = CreateContext();
        var request = await SeedRequestAsync(db);
        var (service, _) = NewService(db);
        var keywords = LitigationKeywordPlanner.Build(request.CompanyName!);

        var job = await service.CreateOrResetJobAsync(request.RequestId, keywords, "individual", request.RequestId.ToString(), CancellationToken.None);

        Assert.Equal(LitigationSearchJobStatus.Pending, job.Status);
        Assert.Contains(request.CompanyName!, job.KeywordsJson);
        Assert.Null(job.VendorJobId);
    }

    // Each test below creates the job on one AppDbContext, then processes it on a *separate, fresh* one —
    // exactly like production, where CreateOrResetJobAsync runs in a controller's request-scoped context and
    // ProcessAsync runs later in the worker's own DI scope. Reusing one context for both would hide a real
    // effect behind a test-only artifact: EF's identity map returns the already-tracked (stale) entity
    // instead of re-reading the row ExecuteUpdateAsync just changed (the exact caveat
    // CalculationAiAuditClaimAndRecoveryTests documents for the same claim pattern).

    [Fact]
    public async Task ProcessAsync_happy_path_authenticates_registers_polls_and_stores_the_raw_report()
    {
        long jobId;
        await using (var setupDb = CreateContext())
        {
            var request = await SeedRequestAsync(setupDb);
            var job = await NewService(setupDb).Service.CreateOrResetJobAsync(
                request.RequestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", "cust-1", CancellationToken.None);
            jobId = job.LitigationSearchJobId;
        }

        await using var processDb = CreateContext();
        var (service, handler) = NewService(processDb);
        StubHappyPath(handler);
        await service.ProcessAsync(jobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Completed, reloaded.Status);
        Assert.Equal("vendor-job-1", reloaded.VendorJobId);
        Assert.Equal(BprReportFormat.Json, reloaded.ReportFormat);
        Assert.NotNull(reloaded.RawReportBytes);
        Assert.NotNull(reloaded.RawResponseHash);
        Assert.Equal(64, reloaded.RawResponseHash!.Length);
        Assert.NotNull(reloaded.CompletedUtc);

        Assert.Contains(handler.Requests, r => r.RequestUri!.AbsolutePath.Contains("bprjob/register"));
    }

    [Fact]
    public async Task ProcessAsync_does_not_re_register_once_a_vendor_job_id_is_already_recorded()
    {
        long jobId;
        await using (var setupDb = CreateContext())
        {
            var request = await SeedRequestAsync(setupDb);
            var job = await NewService(setupDb).Service.CreateOrResetJobAsync(
                request.RequestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", "cust-1", CancellationToken.None);
            jobId = job.LitigationSearchJobId;

            // Simulate a crash between registering and the first successful poll: VendorJobId is already
            // set, but the job never reached Completed — as RecoverStaleWorkAsync would leave it.
            job.VendorJobId = "vendor-job-1";
            job.RegisteredUtc = DateTime.UtcNow;
            job.Status = LitigationSearchJobStatus.Pending;
            await setupDb.SaveChangesAsync(CancellationToken.None);
        }

        await using var processDb = CreateContext();
        var (service, handler) = NewService(processDb);
        StubHappyPath(handler);
        await service.ProcessAsync(jobId, CancellationToken.None);

        var registerCalls = handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("bprjob/register"));
        Assert.Equal(0, registerCalls);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Completed, reloaded.Status);
    }

    [Fact]
    public async Task ProcessAsync_fails_closed_without_retrying_when_a_prior_registration_attempt_never_confirmed()
    {
        // Reviewer finding on PR #251: registration is not crash-idempotent — if the process dies after BPR
        // accepts the call but before VendorJobId is persisted, a blind retry would register a second vendor
        // job (no idempotency key or lookup-by-customer endpoint exists in the confirmed contract). This
        // proves the fix: a job whose RegistrationAttemptedUtc is set but VendorJobId is still null fails
        // immediately, with an actionable message, and never calls bprjob/register again — even though
        // MaxAttempts would otherwise allow a retry.
        var options = new BprLitigationOptions
        {
            BaseUrl = "https://bpr.test/", Id = "app", SecretKey = "secret",
            PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 3 // would normally retry twice more
        };

        long jobId;
        await using (var setupDb = CreateContext())
        {
            var request = await SeedRequestAsync(setupDb);
            var job = await NewService(setupDb, options).Service.CreateOrResetJobAsync(
                request.RequestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", "cust-1", CancellationToken.None);
            jobId = job.LitigationSearchJobId;

            // Simulate the crash window itself: an earlier attempt persisted RegistrationAttemptedUtc right
            // before calling bprjob/register, then the process died before VendorJobId could be recorded.
            job.RegistrationAttemptedUtc = DateTime.UtcNow.AddMinutes(-5);
            await setupDb.SaveChangesAsync(CancellationToken.None);
        }

        await using var processDb = CreateContext();
        var (service, handler) = NewService(processDb, options);
        StubHappyPath(handler); // would succeed if called — proves the refusal is deliberate, not incidental
        await service.ProcessAsync(jobId, CancellationToken.None);

        var registerCalls = handler.Requests.Count(r => r.RequestUri!.AbsolutePath.Contains("bprjob/register"));
        Assert.Equal(0, registerCalls);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Failed, reloaded.Status);
        Assert.Equal(1, reloaded.AttemptCount); // never retried despite MaxAttempts=3
        Assert.Contains("Manual reconciliation required", reloaded.FailureReason);
        Assert.Null(reloaded.VendorJobId);
    }

    [Fact]
    public async Task ProcessAsync_exhausts_attempts_and_ends_Failed_when_authentication_always_fails()
    {
        var options = new BprLitigationOptions
        {
            BaseUrl = "https://bpr.test/", Id = "app", SecretKey = "secret",
            PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 1 // fail on the very first attempt
        };

        long jobId;
        await using (var setupDb = CreateContext())
        {
            var request = await SeedRequestAsync(setupDb);
            var job = await NewService(setupDb, options).Service.CreateOrResetJobAsync(
                request.RequestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", "cust-1", CancellationToken.None);
            jobId = job.LitigationSearchJobId;
        }

        await using var processDb = CreateContext();
        var (service, handler) = NewService(processDb, options);
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await service.ProcessAsync(jobId, CancellationToken.None);

        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Failed, reloaded.Status);
        Assert.NotNull(reloaded.FailureReason);
        Assert.Equal(1, reloaded.AttemptCount);
    }

    [Fact]
    public async Task ProcessAsync_eventually_completes_after_one_pending_poll()
    {
        var options = new BprLitigationOptions
        {
            BaseUrl = "https://bpr.test/", Id = "app", SecretKey = "secret",
            PollIntervalSeconds = 1, PollTimeoutMinutes = 1, MaxAttempts = 2
        };

        long jobId;
        await using (var setupDb = CreateContext())
        {
            var request = await SeedRequestAsync(setupDb);
            var job = await NewService(setupDb, options).Service.CreateOrResetJobAsync(
                request.RequestId, LitigationKeywordPlanner.Build(request.CompanyName!), "individual", "cust-1", CancellationToken.None);
            jobId = job.LitigationSearchJobId;
        }

        await using var processDb = CreateContext();
        var (service, handler) = NewService(processDb, options);
        handler.OnPath("sec/authenticate", _ => JsonResponse("""{"jwt":"token-1"}"""));
        handler.OnPath("bprjob/register", _ => JsonResponse("""{"job_id":"vendor-job-1"}"""));

        var pollCount = 0;
        handler.OnPath("report/job/", _ =>
        {
            pollCount++;
            return pollCount == 1 ? JsonResponse("""{"status":"processing"}""") : JsonResponse("""{"cases":[]}""");
        });

        await service.ProcessAsync(jobId, CancellationToken.None);

        Assert.Equal(2, pollCount);
        await using var verifyDb = CreateContext();
        var reloaded = await verifyDb.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId);
        Assert.Equal(LitigationSearchJobStatus.Completed, reloaded.Status);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string PathSuffix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public void OnPath(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((pathSuffix, respond));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var route = _routes.FirstOrDefault(r => request.RequestUri!.AbsolutePath.Contains(r.PathSuffix, StringComparison.Ordinal));
            return Task.FromResult(route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) }
                : route.Respond(request));
        }
    }
}
