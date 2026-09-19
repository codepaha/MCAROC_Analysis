using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Services.AutoFetch;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Protocol-level behaviour of the reference-tool client against a stubbed HTTP handler — no
/// network. The signing/bid rules are the two things the whole integration hinges on, and every binary
/// endpoint must be judged by file signature (the tool answers "not unlocked" / "logged out" with 200).</summary>
public class ReferenceToolClientTests : IDisposable
{
    private const string Cin = "L45200MH1995PLC093041";
    private const string KeyHex = "6b65792d666f722d7465737473"; // "key-for-tests"
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "reference-tool-tests-" + Guid.NewGuid().ToString("N"));

    public ReferenceToolClientTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    // ── Pure rules ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Bid_is_sha256_of_uppercased_cin()
    {
        // Vector taken from the tool's own search response for this CIN.
        Assert.Equal("36802af6c2d3e1f63b432aa7d5413d5959eeeea57d8287ff1638b73ee5854e01", ReferenceToolClient.ComputeBid(Cin));
        Assert.Equal(ReferenceToolClient.ComputeBid(Cin), ReferenceToolClient.ComputeBid("  l45200mh1995plc093041 "));
    }

    [Fact]
    public void Jwt_is_hs256_over_header_dot_payload_with_the_decoded_key()
    {
        var key = ReferenceToolClient.DecodeKey(KeyHex);
        Assert.Equal("key-for-tests", Encoding.ASCII.GetString(key));

        var token = ReferenceToolClient.SignJwt(new { action = "getNameHints", q = "abc", offset = 0, limit = 10 }, key);
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("{\"alg\":\"HS256\",\"typ\":\"JWT\"}", Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));

        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        Assert.Equal("getNameHints", payload.RootElement.GetProperty("action").GetString());
        Assert.Equal(10, payload.RootElement.GetProperty("limit").GetInt32());

        var expected = HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        Assert.Equal(expected, Base64UrlDecode(parts[2]));
    }

    [Fact]
    public void Non_hex_key_is_used_verbatim()
    {
        Assert.Equal(Encoding.UTF8.GetBytes("plain-secret"), ReferenceToolClient.DecodeKey("plain-secret"));
    }

    [Fact]
    public void Registry_sections_are_json_encoded_strings_with_data_and_totals()
    {
        var registry = ReferenceToolClient.ParseRegistry(RegistryJson(Sec("annual", 3, Doc("a1", "Form PAS-3", attachments: 1), Doc("a2", "Form AOC-4")), Sec("charge", 1, Doc("c1", "Form CHG-1"))));

        Assert.Equal(4, registry.Sections.Count); // all four, even the two absent from the response
        var annual = registry.Sections.Single(s => s.Key == "annual");
        Assert.Equal("Financial Documents", annual.FolderName);
        Assert.Equal(3, annual.TotalCount);
        Assert.Equal(2, annual.Documents.Count);
        var pas3 = annual.Documents.Single(d => d.DocId == "a1");
        Assert.Equal("Form PAS-3", pas3.Name);
        Assert.Equal("214/L45200MH1995PLC093041/mca/a1.pdf", pas3.AwsPath);
        Assert.Single(pas3.Attachments);
        Assert.Equal("a1-att-1.pdf", pas3.Attachments[0].Name);
        Assert.Equal(1, registry.Sections.Single(s => s.Key == "charge").Documents.Count);
        Assert.Empty(registry.Sections.Single(s => s.Key == "scanned").Documents);
        Assert.Equal(4, registry.TotalCount);
        Assert.Equal(3, registry.ListedCount);
    }

    // ── HTTP behaviour ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unconfigured_client_refuses_every_call()
    {
        var client = NewClient(new StubHandler(), configured: false);
        Assert.False(client.IsConfigured);
        await Assert.ThrowsAsync<ReferenceToolException>(() => client.CheckSessionAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ReferenceToolException>(() => client.SearchCompaniesAsync("abc", 5, CancellationToken.None));
    }

    [Fact]
    public async Task Signed_calls_fetch_the_key_once_and_send_cookie_and_versions()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/jwt/service.php", _ => Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
        handler.OnPath("server/user/userDetailsService.php", _ => Json("{\"id\":265271,\"name\":\"Analyst\"}"));
        handler.OnPath("server/common/search/service.php", _ => Json("{\"data\":[{\"legal_name\":\"LODHA DEVELOPERS LIMITED\",\"cin\":\"l45200mh1995plc093041\",\"bid\":\"b\",\"status\":\"Active\",\"company_type\":\"Public\"}]}"));
        var client = NewClient(handler);

        var session = await client.CheckSessionAsync(CancellationToken.None);
        Assert.True(session.IsValid);
        Assert.Equal("265271", session.UserId);

        var hits = await client.SearchCompaniesAsync("lodha", 10, CancellationToken.None);
        var hit = Assert.Single(hits);
        Assert.Equal(Cin, hit.Cin);
        Assert.Equal("LODHA DEVELOPERS LIMITED", hit.LegalName);

        Assert.Equal(1, handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("jwt/service.php")));
        var search = handler.Requests.Single(r => r.RequestUri!.AbsolutePath.EndsWith("search/service.php"));
        Assert.Equal("PHPSESSID=abc; user=x", search.Headers.GetValues("Cookie").Single());
        var query = System.Web.HttpUtility.ParseQueryString(search.RequestUri!.Query);
        Assert.Equal("3.1.6", query["v"]);
        Assert.Equal("8.1.26", query["cv"]);
        var payload = DecodePayload(query["qp"]!);
        Assert.Equal("getNameHints", payload.GetProperty("action").GetString());
        Assert.Equal("lodha", payload.GetProperty("q").GetString());
        Assert.Equal(10, payload.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Logged_out_session_is_reported_invalid_not_thrown()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/jwt/service.php", _ => Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
        handler.OnPath("server/user/userDetailsService.php", _ => Html("<!doctype html><html>login</html>"));
        var session = await NewClient(handler).CheckSessionAsync(CancellationToken.None);
        Assert.False(session.IsValid);
        Assert.Contains("non-JSON", session.Detail);
    }

    [Fact]
    public async Task Automated_login_triggers_when_session_is_invalid_and_credentials_are_configured()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/jwt/service.php", _ => Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
        
        var userDetailsCalls = 0;
        handler.OnPath("server/user/userDetailsService.php", _ =>
        {
            userDetailsCalls++;
            return userDetailsCalls == 1
                ? Html("<!doctype html><html>login</html>") // first check fails
                : Json("{\"id\":265271,\"name\":\"Namashree\"}"); // second check after login succeeds
        });

        handler.OnPath("server/user/login.php", req =>
        {
            var resp = Json("{\"id\":265271,\"user_name\":\"Namashree\"}");
            resp.Headers.Add("Set-Cookie", "PHPSESSID=fresh-sess-123; path=/");
            resp.Headers.Add("Set-Cookie", "user=fresh-user-token; path=/");
            return resp;
        });

        var client = NewClient(handler, configured: true, username: "dharmendra@test.com", password: "SecretPassword123");
        var session = await client.CheckSessionAsync(CancellationToken.None);

        Assert.True(session.IsValid);
        Assert.Equal("265271", session.UserId);
        Assert.Equal(2, userDetailsCalls);

        var loginReq = Assert.Single(handler.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("login.php")));
        Assert.Equal(HttpMethod.Post, loginReq.Method);
        Assert.Contains("fresh-sess-123", client.GetActiveSessionCookie());
    }

    [Fact]
    public async Task Workbook_export_is_judged_by_file_signature()
    {
        var handler = new StubHandler();
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(new byte[600]).ToArray();
        handler.OnPath("server/common/publishing/service.php", req =>
            req.RequestUri!.Query.Contains("CHARGE") ? Json("{\"status\":\"locked\",\"message\":\"Unlock the company\"}") : Bytes(ole, "application/vnd.ms-excel"));
        var client = NewClient(handler);

        var path = await client.DownloadWorkbookAsync(Cin, "bid", ReferenceWorkbookKind.Corporate, Path.Combine(_tempDir, "roc"), CancellationToken.None);
        Assert.EndsWith(".xls", path);
        Assert.Equal(ole.Length, new FileInfo(path).Length);
        var corporate = handler.Requests.First(r => r.RequestUri!.AbsolutePath.EndsWith("publishing/service.php"));
        var printParams = System.Web.HttpUtility.ParseQueryString(corporate.RequestUri!.Query)["printParams"]!;
        using var pp = JsonDocument.Parse(printParams);
        Assert.Equal("ALL", pp.RootElement.GetProperty("context").GetString());
        Assert.Equal(Cin, pp.RootElement.GetProperty("cin").GetString());
        Assert.StartsWith("bid_", pp.RootElement.GetProperty("token").GetString());

        var ex = await Assert.ThrowsAsync<ReferenceToolException>(() =>
            client.DownloadWorkbookAsync(Cin, "bid", ReferenceWorkbookKind.Charge, Path.Combine(_tempDir, "charge"), CancellationToken.None));
        Assert.Contains("not unlocked", ex.Message);
        Assert.Contains("Unlock the company", ex.Message);
        Assert.False(File.Exists(Path.Combine(_tempDir, "charge.download")));
    }

    [Fact]
    public async Task Pdf_download_requires_pdf_signature_and_passes_key_and_user()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/docService/service.php", req =>
            req.RequestUri!.Query.Contains("bad") ? Json("{\"error\":\"no such key\"}") : Bytes("%PDF-1.4\n%fake\n"u8.ToArray(), "application/pdf"));
        var client = NewClient(handler);

        var dest = Path.Combine(_tempDir, "doc", "main.pdf");
        await client.DownloadPdfAsync("bid", "265271", "214/x/good.pdf", "goodv1", dest, CancellationToken.None);
        Assert.True(File.Exists(dest));
        var query = System.Web.HttpUtility.ParseQueryString(handler.Requests.Last().RequestUri!.Query);
        Assert.Equal("downloadPdf", query["action"]);
        Assert.Equal("214/x/good.pdf", query["key"]);
        Assert.Equal("goodv1", query["did"]);
        Assert.Equal("265271", query["userId"]);
        Assert.Equal("b2c", query["platform"]);

        await Assert.ThrowsAsync<ReferenceToolException>(() =>
            client.DownloadPdfAsync("bid", "265271", "214/x/bad.pdf", "badv1", Path.Combine(_tempDir, "doc", "bad.pdf"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_tempDir, "doc", "bad.pdf")));
    }

    /// <summary>A response that declares (via Content-Length) more than the configured cap is refused
    /// before a single byte is written to disk — no ".download" temp file, no partial file, nothing.</summary>
    [Fact]
    public async Task Pdf_download_with_a_declared_length_over_the_cap_is_refused_before_writing_anything()
    {
        var oversized = new byte[2000];
        "%PDF-1.4\n"u8.ToArray().CopyTo(oversized, 0);
        var handler = new StubHandler();
        handler.OnPath("server/common/docService/service.php", _ => Bytes(oversized, "application/pdf"));
        var client = NewClient(handler, maxResponseBytes: 1000);

        var dest = Path.Combine(_tempDir, "doc", "oversized.pdf");
        var ex = await Assert.ThrowsAsync<ReferenceToolException>(() =>
            client.DownloadPdfAsync("bid", "265271", "214/x/big.pdf", "bigv1", dest, CancellationToken.None));

        Assert.Contains("2,000", ex.Message);
        Assert.Contains("1,000", ex.Message);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".download"));
    }

    /// <summary>A response that omits Content-Length (the tool's endpoints don't always send one, and a
    /// chunked/evasive response can't be trusted to) must still be caught by the streaming loop's own
    /// running-total check, and its partial file cleaned up — this is the finding directly: writing an
    /// unbounded external response to disk before any size check ran.</summary>
    [Fact]
    public async Task Pdf_download_with_no_declared_length_is_still_caught_mid_stream_and_cleaned_up()
    {
        var oversized = new byte[2000];
        "%PDF-1.4\n"u8.ToArray().CopyTo(oversized, 0);
        var handler = new StubHandler();
        handler.OnPath("server/common/docService/service.php", _ => StreamingBytesWithNoDeclaredLength(oversized, "application/pdf"));
        var client = NewClient(handler, maxResponseBytes: 1000);

        var dest = Path.Combine(_tempDir, "doc", "oversized-streamed.pdf");
        var ex = await Assert.ThrowsAsync<ReferenceToolException>(() =>
            client.DownloadPdfAsync("bid", "265271", "214/x/big.pdf", "bigv1", dest, CancellationToken.None));

        Assert.Contains("exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".download"));
    }

    /// <summary>Same enforcement applies to the workbook export, not only filing PDFs — both funnel
    /// through the same StreamToFileAsync.</summary>
    [Fact]
    public async Task Workbook_download_over_the_cap_is_also_refused_and_cleaned_up()
    {
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.Concat(new byte[2000]).ToArray();
        var handler = new StubHandler();
        handler.OnPath("server/common/publishing/service.php", _ => Bytes(ole, "application/vnd.ms-excel"));
        var client = NewClient(handler, maxResponseBytes: 1000);

        var dest = Path.Combine(_tempDir, "oversized-roc");
        await Assert.ThrowsAsync<ReferenceToolException>(() =>
            client.DownloadWorkbookAsync(Cin, "bid", ReferenceWorkbookKind.Corporate, dest, CancellationToken.None));

        Assert.False(File.Exists(dest + ".xls"));
        Assert.False(File.Exists(dest + ".download"));
    }

    [Fact]
    public async Task Registry_pages_with_offset_until_a_page_adds_nothing()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/jwt/service.php", _ => Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
        handler.OnPath("server/common/docService/service.php", req =>
        {
            var payload = DecodePayload(System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query)["qp"]!);
            var offset = payload.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
            return offset == 0
                ? Json(RegistryJson(Sec("charge", 3, Doc("c1", "Form CHG-1"), Doc("c2", "Form CHG-4"))))
                : Json(RegistryJson(Sec("charge", 3, Doc("c2", "Form CHG-4"), Doc("c3", "Form CHG-9")))); // overlaps + one new, then repeats
        });
        var registry = await NewClient(handler).GetReferenceDocumentsAsync("bid", CancellationToken.None);

        var charge = registry.Sections.Single(s => s.Key == "charge");
        Assert.Equal(3, charge.TotalCount);
        Assert.Equal(new[] { "c1", "c2", "c3" }, charge.Documents.Select(d => d.DocId).OrderBy(x => x));
        // first page, page at offset 2 (adds c3 → complete), no further request needed
        var registryCalls = handler.Requests.Where(r => r.RequestUri!.AbsolutePath.EndsWith("docService/service.php")).ToList();
        Assert.Equal(2, registryCalls.Count);
        var second = DecodePayload(System.Web.HttpUtility.ParseQueryString(registryCalls[1].RequestUri!.Query)["qp"]!);
        Assert.Equal(2, second.GetProperty("offset").GetInt32());
        Assert.Equal(2, second.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Registry_paging_stops_when_the_endpoint_ignores_offset()
    {
        var handler = new StubHandler();
        handler.OnPath("server/common/jwt/service.php", _ => Json($"{{\"jwtToken\":\"{KeyHex}\"}}"));
        handler.OnPath("server/common/docService/service.php", _ => Json(RegistryJson(Sec("scanned", 500, Doc("s1", "Old deed")))));
        var registry = await NewClient(handler).GetReferenceDocumentsAsync("bid", CancellationToken.None);

        Assert.Equal(500, registry.TotalCount);
        Assert.Equal(1, registry.ListedCount);
        Assert.Equal(2, handler.Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("docService/service.php"))); // one retry, then gave up
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static ReferenceToolClient NewClient(StubHandler handler, bool configured = true, long? maxResponseBytes = null, string username = "", string password = "")
    {
        var options = Options.Create(new ReferenceToolOptions
        {
            BaseUrl = configured ? "https://reference-tool.test" : "",
            SessionCookie = configured ? "PHPSESSID=abc; user=x" : "",
            Username = username,
            Password = password,
            MaxResponseBytes = maxResponseBytes ?? new ReferenceToolOptions().MaxResponseBytes,
        });
        return new ReferenceToolClient(new HttpClient(handler), options, NullLogger<ReferenceToolClient>.Instance);
    }

    private static string Doc(string id, string name, int attachments = 0)
    {
        var atts = string.Join(",", Enumerable.Range(1, attachments).Select(i =>
            $"{{\"mcaName\":\"{id}-att-{i}.pdf\",\"awsPath\":\"214/L45200MH1995PLC093041/mca/{id}_{id}-att-{i}.pdf\"}}"));
        return $"{{\"docId\":\"{id}\",\"mcaName\":\"{name}\",\"formName\":\"X\",\"documentDate\":\"2026-07-23T05:30:00+05:30\",\"size\":12.5," +
               $"\"awsPath\":\"214/L45200MH1995PLC093041/mca/{id}.pdf\",\"section\":\"S\",\"attachments\":[{atts}]}}";
    }

    private static (string Key, int Total, string[] Docs) Sec(string key, int total, params string[] docs) => (key, total, docs);

    /// <summary>Mirrors the tool's shape: each section value is a JSON *string* holding {data, totalCount}.</summary>
    private static string RegistryJson(params (string Key, int Total, string[] Docs)[] sections)
    {
        var parts = sections.Select(s =>
        {
            var inner = $"{{\"data\":[{string.Join(",", s.Docs)}],\"totalCount\":{s.Total}}}";
            return $"\"{s.Key}\":{JsonSerializer.Serialize(inner)}";
        });
        return "{" + string.Join(",", parts) + "}";
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Bytes(byte[] body, string contentType)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    /// <summary>Same bytes as <see cref="Bytes"/> but via StreamContent, which reports no Content-Length —
    /// the shape a chunked/evasive response takes, forcing the streaming loop's own running-total check to
    /// be what catches an oversized body rather than the declared-length fast-fail path.</summary>
    private static HttpResponseMessage StreamingBytesWithNoDeclaredLength(byte[] body, string contentType)
    {
        var content = new StreamContent(new MemoryStream(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Headers.ContentLength = null;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private static JsonElement DecodePayload(string jwt) =>
        JsonDocument.Parse(Base64UrlDecode(jwt.Split('.')[1])).RootElement.Clone();

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string PathSuffix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        public void OnPath(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((pathSuffix, respond));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var route = _routes.FirstOrDefault(r => request.RequestUri!.AbsolutePath.EndsWith(r.PathSuffix, StringComparison.Ordinal));
            return Task.FromResult(route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) }
                : route.Respond(request));
        }
    }
}
