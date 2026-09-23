using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>An in-memory stand-in for the reference tool's unlock/refresh endpoints
/// (docs/reference-tool-refresh-unlock-contract.md), dispatching on the signed payload's <c>action</c>. Holds
/// one company's state and records every call in order, so tests can assert on sequencing — in particular that
/// nothing is exported before a refresh has completed.</summary>
public sealed class FakeReferenceTool : HttpMessageHandler
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

    public ConcurrentQueue<string> Calls { get; } = new();
    public int Count(string action) => Calls.Count(c => c == action);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("jwt/service.php", StringComparison.Ordinal))
            return Task.FromResult(Json(new { jwtToken = KeyHex }));
        if (path.EndsWith("publishing/service.php", StringComparison.Ordinal))
        {
            Calls.Enqueue("publishProbedData");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("export stub") });
        }

        var qp = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["qp"];
        var action = qp is null ? "" : Payload(qp).GetProperty("action").GetString() ?? "";
        Calls.Enqueue(action);
        lock (_sync)
        {
            return Task.FromResult(action switch
            {
                "getUserDetails" => Json(new { id = 265271, user_name = "tester" }),
                "getAssetTeams" => Json(new { teams = new[] { new { teamId = 42, addedAt = AddedAt?.ToString("yyyy-MM-ddTHH:mm:sszzz"), creditUsed = AddedAt is not null } } }),
                "getUpgradeStatusForCompanies" => Json(new { mca_status = McaStatus }),
                "requestProbeDataUpdate" => RequestRefresh(),
                "getDataEntryRequestStatus" => Json(RefreshPending ? new { status = "REQUESTED" } : new { status = "NO PENDING REQUEST" }),
                "getDataStatus" => Json(new { downloaded = DataAsOf?.ToString("yyyy-MM-ddTHH:mm:sszzz") }),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + action) }
            });
        }
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
