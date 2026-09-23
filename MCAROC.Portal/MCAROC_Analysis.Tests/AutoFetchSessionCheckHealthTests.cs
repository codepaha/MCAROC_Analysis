using System.Net;
using System.Text;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Excel;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>PR #277 review: the job's initial session check must go through breaker health reporting and
/// keep its failure kind — a transient failure leaves the job Queued, an auth rejection opens the breaker,
/// and neither is swallowed into a kindless "session not valid" failure. Runs the real
/// <see cref="AutoFetchJobService.ProcessAsync"/> against the SQLEXPRESS test database.</summary>
public sealed class AutoFetchSessionCheckHealthTests : IAsyncLifetime
{
    private const string KeyHex = "6b65792d666f722d7465737473";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "autofetch-session-health-" + Guid.NewGuid().ToString("N"));

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDir);
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
        // The claim predicate reads the real breaker row; a stale Open row left by an earlier local run
        // would stop ProcessAsync from ever claiming these jobs.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM IntegrationHealths WHERE Name = 'ReferenceTool'");
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Transient_http_failure_on_the_session_check_leaves_the_job_queued_and_reports_a_non_auth_failure()
    {
        var (job, health) = await RunAsync(path => path.EndsWith("userDetailsService.php")
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("down") }
            : null);

        Assert.Equal(AutoFetchJobStatus.Queued, job.Status);
        Assert.Null(job.FailureReason);
        var failure = Assert.Single(health.Reports);
        Assert.False(failure.Success);
        Assert.False(failure.AuthRejected);
    }

    [Fact]
    public async Task Transport_exception_on_the_session_check_leaves_the_job_queued()
    {
        var (job, health) = await RunAsync(path => path.EndsWith("userDetailsService.php")
            ? throw new HttpRequestException("connection refused")
            : null);

        Assert.Equal(AutoFetchJobStatus.Queued, job.Status);
        var failure = Assert.Single(health.Reports);
        Assert.False(failure.Success);
        Assert.False(failure.AuthRejected);
    }

    [Fact]
    public async Task Rejected_login_on_the_session_check_fails_the_job_and_reports_auth_rejected()
    {
        var (job, health) = await RunAsync(path =>
            path.EndsWith("userDetailsService.php") ? Html("<html>login</html>")
            : path.EndsWith("login.php") ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"error\":\"bad credentials\"}") }
            : null,
            username: "analyst@test.com", password: "wrong");

        Assert.Equal(AutoFetchJobStatus.Failed, job.Status);
        var failure = Assert.Single(health.Reports);
        Assert.False(failure.Success);
        Assert.True(failure.AuthRejected);
    }

    [Fact]
    public async Task Expired_session_without_auto_login_fails_the_job_and_reports_the_failure()
    {
        var (job, health) = await RunAsync(path => path.EndsWith("userDetailsService.php") ? Html("<html>login</html>") : null);

        Assert.Equal(AutoFetchJobStatus.Failed, job.Status);
        Assert.Contains("session is not valid", job.FailureReason);
        var failure = Assert.Single(health.Reports);
        Assert.False(failure.Success);
        Assert.False(failure.AuthRejected);
    }

    private async Task<(AutoFetchJob Job, RecordingIntegrationHealthService Health)> RunAsync(
        Func<string, HttpResponseMessage?> respond, string username = "", string password = "")
    {
        await using var db = CreateContext();
        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            Username = username,
            Password = password
        });
        var health = new RecordingIntegrationHealthService();
        var client = new ReferenceToolClient(new HttpClient(new StubHandler(respond)), options, new ReferenceToolSession(), health, NullLogger<ReferenceToolClient>.Instance);
        var jobs = new AutoFetchJobService(db, client, options, new FileValidationService(new ExcelSheetReader()),
            null!, null!, null!, null!, new FakeEnv(_tempDir), NullLogger<AutoFetchJobService>.Instance);

        var jobId = await SeedQueuedJobAsync(db);
        await jobs.ProcessAsync(jobId, CancellationToken.None);

        await using var verify = CreateContext();
        var result = await verify.AutoFetchJobs.AsNoTracking().SingleAsync(j => j.AutoFetchJobId == jobId);

        // Don't leave a Queued job behind for some later host's recovery sweep to pick up.
        await verify.AutoFetchJobs.Where(j => j.AutoFetchJobId == jobId).ExecuteDeleteAsync();
        await verify.Requests.Where(r => r.RequestId == result.RequestId).ExecuteDeleteAsync();
        return (result, health);
    }

    private static async Task<long> SeedQueuedJobAsync(AppDbContext db)
    {
        var client = new Client { ClientCode = "SES" + Guid.NewGuid().ToString("N")[..7], ClientName = "Session Health Co", CreatedDate = DateTime.UtcNow };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Session Health Test Company",
            Cin = "U45203OR1995PLC003982", RequestNumber = $"SES-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.Created, CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        var job = new AutoFetchJob
        {
            RequestId = request.RequestId, Cin = request.Cin!, Bid = ReferenceToolClient.ComputeBid(request.Cin!),
            Status = AutoFetchJobStatus.Queued, WarningsJson = "[]", CreatedUtc = DateTime.UtcNow
        };
        db.AutoFetchJobs.Add(job);
        await db.SaveChangesAsync();
        return job.AutoFetchJobId;
    }

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    public sealed class RecordingIntegrationHealthService : IIntegrationHealthService
    {
        public List<(bool Success, bool AuthRejected, string? Error)> Reports { get; } = [];

        public Task ReportFailureAsync(IntegrationName name, DateTime callStartUtc, string? error, bool authRejected, int threshold, TimeSpan probeLease, CancellationToken ct)
        {
            Reports.Add((false, authRejected, error));
            return Task.CompletedTask;
        }

        public Task ReportSuccessAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct)
        {
            Reports.Add((true, false, null));
            return Task.CompletedTask;
        }

        public Task<bool> TryClaimHalfOpenProbeAsync(IntegrationName name, TimeSpan probeLease, CancellationToken ct) => Task.FromResult(false);
        public Task TryCloseAfterProbeAsync(IntegrationName name, DateTime callStartUtc, CancellationToken ct) => Task.CompletedTask;
        public Task<IntegrationHealth?> GetAsync(IntegrationName name, CancellationToken ct) => Task.FromResult<IntegrationHealth?>(null);
        public Task<bool> IsOpenAsync(IntegrationName name, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class StubHandler(Func<string, HttpResponseMessage?> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($"{{\"jwtToken\":\"{KeyHex}\"}}", Encoding.UTF8, "application/json")
                });
            return Task.FromResult(respond(path)
                ?? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) });
        }
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
