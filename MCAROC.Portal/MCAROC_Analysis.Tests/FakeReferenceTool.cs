using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>An in-memory stand-in for the reference tool's unlock/refresh endpoints
/// (docs/reference-tool-refresh-unlock-contract.md), dispatching on the signed payload's <c>action</c>. Holds
/// one company's state and records every call in order, so tests can assert on sequencing — in particular that
/// nothing is exported before a refresh has completed. Answers only for its own company (<paramref name="bid"/>);
/// any other company gets a 404, so a worker polling some other test's company can't change this one's state.</summary>
public sealed class FakeReferenceTool(string bid) : HttpMessageHandler
{
    private const string KeyHex = "6b65792d666f722d7465737473";
    private readonly object _sync = new();

    public DateTimeOffset? AddedAt { get; set; }
    public DateTimeOffset? DataAsOf { get; set; }
    public bool RefreshPending { get; set; }
    public string McaStatus { get; set; } = "NORMAL";
    /// <summary>What a refresh request does to the tool's state; default: marks a refresh pending.</summary>
    public Action<FakeReferenceTool>? OnRefreshRequested { get; set; }
    public HttpStatusCode RefreshRequestStatus { get; set; } = HttpStatusCode.OK;

    /// <summary>What <c>getCompanyPreview</c> reports as the company's CIN (the pre-spend identity check).</summary>
    public string? PreviewCin { get; set; }
    public string UnlockMcaStatus { get; set; } = "NORMAL";
    public HttpStatusCode AddAssetStatus { get; set; } = HttpStatusCode.OK;
    /// <summary>The time a successful unlock is stamped with — tests on a fake clock point this at it.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;
    /// <summary>Delays for race tests: stagger callers' last pre-spend check, and slow the paid call itself.</summary>
    public Func<TimeSpan>? UnlockCheckDelay { get; set; }
    public TimeSpan AddAssetDelay { get; set; }

    public ConcurrentQueue<string> Calls { get; } = new();
    public int Count(string action) => Calls.Count(c => c == action);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (UnlockCheckDelay is { } delay && request.RequestUri!.Query.Length > 0 && request.RequestUri.AbsolutePath.EndsWith("mcastatus/service.php", StringComparison.Ordinal))
            await Task.Delay(delay(), cancellationToken);
        return await DispatchAsync(request, cancellationToken);
    }

    private Task<HttpResponseMessage> DispatchAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
            return Task.FromResult(Json(new { jwtToken = KeyHex }));
        if (path.EndsWith("publishing/service.php", StringComparison.Ordinal))
        {
            Calls.Enqueue("publishProbedData");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("export stub") });
        }

        if (request.Method == HttpMethod.Post && path.EndsWith("user/service.php", StringComparison.Ordinal))
            return AddAssetAsync(request, cancellationToken);

        var qp = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["qp"];
        var payload = qp is null ? default : Payload(qp);
        var action = qp is null ? "" : payload.GetProperty("action").GetString() ?? "";
        if (qp is not null && payload.TryGetProperty("bid", out var requestedBid) && requestedBid.GetString() != bid)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("other company") });
        Calls.Enqueue(action);
        lock (_sync)
        {
            return Task.FromResult(action switch
            {
                "getUserDetails" => Json(new { id = 265271, user_name = "tester" }),
                "getAssetTeams" => Json(new { teams = new[] { new { teamId = 42, addedAt = AddedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"), creditUsed = AddedAt is not null } } }),
                "getUpgradeStatusForCompanies" => Json(new { mca_status = McaStatus }),
                "getUpgradeStatusForUnlockingAsset" => Json(new { mca_status = UnlockMcaStatus }),
                "getCompanyPreview" => Json(new { about_company = new { summary = new
                {
                    cin = PreviewCin, legal_name = "Fake Test Company Private Limited", company_llp_status = "Active",
                    registered_address = new { state = "Maharashtra" }
                } } }),
                "requestProbeDataUpdate" => RequestRefresh(),
                "getDataEntryRequestStatus" => Json(RefreshPending ? new { status = "REQUESTED" } : new { status = "NO PENDING REQUEST" }),
                "getDataStatus" => Json(new { downloaded = DataAsOf?.ToString("yyyy-MM-ddTHH:mm:sszzz") }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + action) }
            });
        }
    }

    /// <summary><c>addAsset</c>: the paid unlock. Its payload is signed into the form body, not the query.</summary>
    private async Task<HttpResponseMessage> AddAssetAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var form = System.Web.HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
        var payload = Payload(form["pp"]!);
        if (payload.GetProperty("bid").GetString() != bid)
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("other company") };
        Calls.Enqueue("addAsset");
        if (AddAssetDelay > TimeSpan.Zero) await Task.Delay(AddAssetDelay, ct);
        if (AddAssetStatus != HttpStatusCode.OK)
            return new HttpResponseMessage(AddAssetStatus) { Content = new StringContent("down") };
        lock (_sync) { AddedAt ??= Clock(); }
        return Json(new { statusCode = true });
    }

    private HttpResponseMessage RequestRefresh()
    {
        if (RefreshRequestStatus != HttpStatusCode.OK)
            return new HttpResponseMessage(RefreshRequestStatus) { Content = new StringContent("down") };
        if (OnRefreshRequested is { } hook) hook(this); else RefreshPending = true;
        return Json(new { id = "123", status = "PENDING" });
    }

    public ReferenceToolClient NewClient(IIntegrationHealthService? health = null) =>
        new(new HttpClient(this), Options(), new ReferenceToolSession(), health ?? new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);

    /// <summary>The gate's real services (refresh, unlock, admission ledger, coordinator) against the test
    /// database, talking to this fake — what the worker and the approve action resolve in production.</summary>
    public ServiceProvider BuildServices(TimeProvider time, AutoFetchQueue queue, PipelineOptions? pipeline = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(TestDatabase.ConnectionString));
        services.AddSingleton(time);
        services.AddSingleton(queue);
        services.AddSingleton<IOptionsMonitor<PipelineOptions>>(new StaticPipelineOptions(pipeline ?? new PipelineOptions()));
        services.AddSingleton(Options());
        services.AddScoped<IIntegrationHealthService, NoOpIntegrationHealthService>();
        services.AddScoped(_ => NewClient());
        services.AddScoped<IPaidCallAdmission, PaidCallAdmissionService>();
        services.AddScoped<CompanyRefreshService>();
        services.AddScoped<CompanyUnlockService>();
        services.AddScoped<CompanyGateCoordinator>();
        return services.BuildServiceProvider();
    }

    private sealed class StaticPipelineOptions(PipelineOptions value) : IOptionsMonitor<PipelineOptions>
    {
        public PipelineOptions CurrentValue => value;
        public PipelineOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<PipelineOptions, string?> listener) => null;
    }

    public static IOptions<ReferenceToolOptions> Options(bool refreshBeforeFetch = true) =>
        Microsoft.Extensions.Options.Options.Create(new ReferenceToolOptions
        {
            BaseUrl = "https://reference-tool.test",
            SessionCookie = "PHPSESSID=abc",
            UserId = "265271",
            ToolUserName = "tester",
            RefreshBeforeFetch = refreshBeforeFetch
        });

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static JsonElement Payload(string jwt)
    {
        var part = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        part += new string('=', (4 - part.Length % 4) % 4);
        return JsonDocument.Parse(Convert.FromBase64String(part)).RootElement.Clone();
    }
}
