using System.Net;
using System.Net.Sockets;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Preflight;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>#514: the pre-flight check reports each dependency of an E2E round, and is free: it never spends a credit, starts a probe,
/// exports, or registers a litigation search, whatever it finds.</summary>
public sealed class PreflightServiceTests : IAsyncLifetime, IDisposable
{
    private static readonly string[] PaidOrMutatingActions = ["addAsset", "requestProbeData", "requestProbeDataUpdate", "publishProbedData"];
    private readonly TcpListener _bprPort = new(IPAddress.Loopback, 0);

    public PreflightServiceTests() => _bprPort.Start();
    public void Dispose() => _bprPort.Stop();

    public async Task InitializeAsync()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(TestDatabase.ConnectionString).Options);
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private int OpenPort => ((IPEndPoint)_bprPort.LocalEndpoint).Port;

    private static int ClosedPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private sealed class Recorder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Hang : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class Env : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MCAROC_Analysis";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private PreflightService Service(FakeReferenceTool tool, Recorder bpr, int bprPort, Dictionary<string, string?>? settings = null,
        HttpMessageHandler? referenceHandler = null, TimeSpan? timeout = null)
    {
        var bprOptions = new BprLitigationOptions
        {
            BaseUrl = $"http://127.0.0.1:{bprPort}/", Id = "test", SecretKey = "secret", AllowedOrderDocumentHosts = ["localhost"]
        };
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(TestDatabase.ConnectionString));
        services.AddScoped(_ => referenceHandler is null
            ? tool.NewClient()
            : new ReferenceToolClient(new HttpClient(referenceHandler), FakeReferenceTool.Options(), new ReferenceToolSession(),
                new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(bprOptions));
        services.AddScoped(_ => new BprLitigationClient(new HttpClient(bpr) { BaseAddress = new Uri(bprOptions.BaseUrl) },
            Microsoft.Extensions.Options.Options.Create(bprOptions), NullLogger<BprLitigationClient>.Instance));
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>
        {
            ["ApplicationAuth:Username"] = "tester@test", ["ApplicationAuth:PasswordHash"] = "AQAAAAIAAYagAAAAE-test",
            ["LitigationPortals:Agents:Enabled"] = "true", ["LitigationModule:LivePublisher:Enabled"] = "true",
            ["LitigationKeywords:UseGeneratedKeywords"] = "true",
        }).Build();
        return new PreflightService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), config, new Env(),
            NullLogger<PreflightService>.Instance, timeout);
    }

    private static Recorder BprSignsIn() => new(r => r.RequestUri!.AbsolutePath.EndsWith("sec/authenticate")
        ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ0In0.c2ln") }
        : new HttpResponseMessage(HttpStatusCode.NotFound));

    private static PreflightCheck Check(PreflightReport report, string key) => report.Checks.Single(c => c.Key == key);

    [Fact]
    public async Task Every_dependency_is_reported_and_nothing_is_spent_started_or_registered()
    {
        var tool = new FakeReferenceTool("bid");
        var bpr = BprSignsIn();

        var report = await Service(tool, bpr, OpenPort).RunAsync(CancellationToken.None);

        Assert.Equal(["reference-session", "mca-company", "mca-llp", "litigation-api", "order-host", "portal-login", "database", "ai", "court-agents", "keywords", "disk"],
            report.Checks.Select(c => c.Key));
        Assert.Equal(PreflightOutcome.Pass, Check(report, "reference-session").Outcome);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "mca-company").Outcome);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "mca-llp").Outcome);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "litigation-api").Outcome);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "portal-login").Outcome);
        Assert.DoesNotContain("not applied", Check(report, "database").Detail); // AUTO_CLOSE depends on how the test database was created
        Assert.Equal(PreflightOutcome.Pass, Check(report, "court-agents").Outcome);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "keywords").Outcome);

        Assert.Contains("getUpgradeStatusForCompanies", tool.Calls);
        Assert.Contains("getUpgradeStatusForLLPs", tool.Calls);
        Assert.DoesNotContain(tool.Calls, c => PaidOrMutatingActions.Contains(c));
        Assert.Equal(["/sec/authenticate"], bpr.Paths); // signed in only; no search registered
    }

    [Fact]
    public async Task An_mca_alert_is_a_warning_with_its_notice_and_maintenance_is_a_failure()
    {
        var alert = new FakeReferenceTool("bid") { McaStatus = "ALERT" };
        var report = await Service(alert, BprSignsIn(), OpenPort).RunAsync(CancellationToken.None);
        Assert.Equal(PreflightOutcome.Warn, Check(report, "mca-company").Outcome);
        Assert.StartsWith("ALERT", Check(report, "mca-company").Detail);

        var maintenance = new FakeReferenceTool("bid") { McaStatus = "UNDER_MAINTENANCE" };
        report = await Service(maintenance, BprSignsIn(), OpenPort).RunAsync(CancellationToken.None);
        Assert.Equal(PreflightOutcome.Fail, Check(report, "mca-llp").Outcome);
        Assert.Equal(PreflightOutcome.Fail, report.Overall);
    }

    /// <summary>What 2026-10-16 found only mid-run: the litigation service refusing connections.</summary>
    [Fact]
    public async Task A_litigation_service_refusing_connections_fails_without_trying_to_sign_in()
    {
        var bpr = BprSignsIn();
        var report = await Service(new FakeReferenceTool("bid"), bpr, ClosedPort()).RunAsync(CancellationToken.None);

        var check = Check(report, "litigation-api");
        Assert.Equal(PreflightOutcome.Fail, check.Outcome);
        Assert.Contains("refused", check.Detail);
        Assert.Empty(bpr.Paths);
    }

    [Fact]
    public async Task Missing_portal_login_and_ai_settings_fail_and_switched_off_features_warn()
    {
        var report = await Service(new FakeReferenceTool("bid"), BprSignsIn(), OpenPort, settings: []).RunAsync(CancellationToken.None);

        Assert.Equal(PreflightOutcome.Fail, Check(report, "portal-login").Outcome);
        Assert.Equal(PreflightOutcome.Fail, Check(report, "ai").Outcome);
        Assert.Equal(PreflightOutcome.Warn, Check(report, "court-agents").Outcome);
        Assert.Equal(PreflightOutcome.Warn, Check(report, "keywords").Outcome);
    }

    [Fact]
    public async Task A_check_that_does_not_answer_fails_on_its_own_timeout_and_the_rest_still_report()
    {
        var report = await Service(new FakeReferenceTool("bid"), BprSignsIn(), OpenPort, referenceHandler: new Hang(), timeout: TimeSpan.FromSeconds(1))
            .RunAsync(CancellationToken.None);

        Assert.Equal(PreflightOutcome.Fail, Check(report, "reference-session").Outcome);
        Assert.Contains("No answer within", Check(report, "reference-session").Detail);
        Assert.Equal(PreflightOutcome.Pass, Check(report, "litigation-api").Outcome);
    }
}
