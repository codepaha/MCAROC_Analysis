using System.Net;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.Capture;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>#514 capture mode: every live call is written for fixtures with nothing that signs us in or identifies our account, documents pass through
/// unread, and a capture problem never fails the call. The committed fixtures are scanned with the same rules, so a leak fails CI.</summary>
public sealed class IntegrationCaptureTests : IDisposable
{
    private const string KeyHex = "6b65792d666f722d7465737473";
    private const string Username = "someone@example.co.in";
    private const string Password = "S3cret-Password!";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "capture-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private sealed class Tool : HttpMessageHandler
    {
        public static readonly byte[] Pdf = "%PDF-1.4\n%fixture body\n"u8.ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (path.EndsWith("jwt/service.php")) return Task.FromResult(Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
            if (path.EndsWith("login.php"))
            {
                var r = Json("{\"id\":265271,\"user_name\":\"Real Person\",\"email\":\"" + Username + "\"}");
                r.Headers.Add("Set-Cookie", "PHPSESSID=live-session-value; path=/");
                return Task.FromResult(r);
            }
            if (path.EndsWith("mcastatus/service.php"))
                return Task.FromResult(Json("{\"mca_status\":\"ALERT\",\"alert_text\":\"<b>MCA peak filing period.</b>\"}"));
            if (path.EndsWith("docService/service.php"))
            {
                var content = new ByteArrayContent(Pdf);
                content.Headers.ContentType = new("application/pdf");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            }
            return Task.FromResult(Json("{\"id\":265271,\"user_name\":\"Real Person\",\"email\":\"" + Username + "\",\"mobile\":\"9800000000\"}"));
        }
    }

    private ReferenceToolClient Client(HttpMessageHandler inner, string? directory = null) =>
        new(new HttpClient(new CaptureHandler(CaptureService.ReferenceTool, directory ?? _dir, NullLogger<CaptureHandler>.Instance) { InnerHandler = inner }),
            Options.Create(new ReferenceToolOptions { BaseUrl = "https://real-tool.example.com", Username = Username, Password = Password }),
            new ReferenceToolSession(), new NoOpIntegrationHealthService(), NullLogger<ReferenceToolClient>.Instance);

    private string[] Captured() => Directory.GetFiles(_dir, "*.json").Order().Select(File.ReadAllText).ToArray();

    [Fact]
    public async Task A_live_session_is_captured_without_credentials_tokens_or_our_account_and_with_the_company_data()
    {
        var client = Client(new Tool());
        Assert.True((await client.CheckSessionAsync(CancellationToken.None)).IsValid);
        var mcaAllows = await client.IsMcaAvailableForRefreshAsync(isLlp: true, CancellationToken.None);
        var dest = Path.Combine(_dir, "doc.pdf");
        await client.DownloadDocumentAsync("bid", "265271", "214/x/a.pdf", "a1", dest, CancellationToken.None);

        Assert.True(mcaAllows); // ALERT proceeds
        Assert.Equal(Tool.Pdf, await File.ReadAllBytesAsync(dest)); // passed through unread
        var files = Captured();
        Assert.True(files.Length >= 4, $"{files.Length} calls captured");
        var all = string.Join("\n", files);
        foreach (var secret in new[] { Password, Username, KeyHex, "live-session-value", "Real Person", "9800000000", "real-tool.example.com" })
            Assert.DoesNotContain(secret, all);
        foreach (var file in files) Assert.Empty(CaptureRedactor.FindLeaks(file));

        var login = files.Select(f => JsonDocument.Parse(f).RootElement).Single(r => r.GetProperty("action").GetString() == "login");
        Assert.Equal(CaptureRedactor.Redacted, login.GetProperty("payload").GetProperty("p").GetString());
        Assert.StartsWith("https://reference.test/", login.GetProperty("url").GetString());

        var status = files.Select(f => JsonDocument.Parse(f).RootElement).Single(r => r.GetProperty("action").GetString() == "getUpgradeStatusForLLPs");
        Assert.Equal("ALERT", status.GetProperty("body").GetProperty("mca_status").GetString());
        Assert.Equal("<b>MCA peak filing period.</b>", status.GetProperty("body").GetProperty("alert_text").GetString());

        var download = files.Select(f => JsonDocument.Parse(f).RootElement).Single(r => r.GetProperty("action").GetString() == "downloadPdf");
        Assert.Equal("application/pdf", download.GetProperty("contentType").GetString());
        Assert.Equal(JsonValueKind.Null, download.GetProperty("body").ValueKind);
        Assert.Equal(CaptureRedactor.Redacted, download.GetProperty("query").GetProperty("userId").GetString()); // sent in the plain query
        Assert.Equal("214/x/a.pdf", download.GetProperty("query").GetProperty("key").GetString());
    }

    [Fact]
    public async Task The_litigation_sign_in_is_captured_without_its_id_secret_or_token()
    {
        var token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJkYXRhdGVhbSJ9.c2lnbmF0dXJlLXZhbHVl";
        var inner = new LambdaHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(token) });
        var opts = new BprLitigationOptions { BaseUrl = "http://203.0.113.7:2087/", Id = "app-id-value", SecretKey = "the-secret-key" };
        var client = new BprLitigationClient(
            new HttpClient(new CaptureHandler(CaptureService.LitigationApi, _dir, NullLogger<CaptureHandler>.Instance) { InnerHandler = inner }) { BaseAddress = new Uri(opts.BaseUrl) },
            Options.Create(opts), NullLogger<BprLitigationClient>.Instance);

        Assert.Equal(token, await client.AuthenticateAsync(CancellationToken.None));

        var file = Assert.Single(Captured());
        foreach (var secret in new[] { "app-id-value", "the-secret-key", token, "203.0.113.7" }) Assert.DoesNotContain(secret, file);
        Assert.Contains("litigation.test/sec/authenticate", file);
        Assert.Empty(CaptureRedactor.FindLeaks(file));
    }

    [Fact]
    public async Task A_capture_that_cannot_be_written_never_fails_the_call()
    {
        var blocked = Path.Combine(_dir, "not-a-directory");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(blocked, "a file where the capture directory should be");

        Assert.True(await Client(new Tool(), blocked).IsMcaAvailableForRefreshAsync(isLlp: false, CancellationToken.None));
    }

    [Theory]
    [InlineData("{\"t\":\"eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4eHh4In0.abcdefgh\"}", "token")]
    [InlineData("Cookie: PHPSESSID=abcdef123", "cookie")]
    [InlineData("contact someone@example.com", "e-mail")]
    [InlineData("https://some-vendor-host.com/api", "host")]
    [InlineData("http://198.51.100.20:2087/", "address")]
    public void A_leak_is_found_by_kind(string text, string kind) =>
        Assert.Contains(CaptureRedactor.FindLeaks(text), l => l.StartsWith(kind));

    /// <summary>The CI gate: no committed fixture of a live call may hold a token, cookie, e-mail address, real host or IP address.</summary>
    [Fact]
    public void Committed_live_fixtures_hold_nothing_that_signs_us_in_or_names_a_real_host()
    {
        var root = Path.Combine(RepoRoot(), "MCAROC.Portal", "MCAROC_Analysis.Tests", "Fixtures");
        var files = new[] { "reference-tool", "litigation-api" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories)).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.True(CaptureRedactor.FindLeaks(File.ReadAllText(file)) is { Count: 0 } leaks ? true : throw new Xunit.Sdk.XunitException(
                $"{Path.GetRelativePath(root, file)}: {string.Join(", ", CaptureRedactor.FindLeaks(File.ReadAllText(file)))}"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "MCAROC.Portal"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }
}
