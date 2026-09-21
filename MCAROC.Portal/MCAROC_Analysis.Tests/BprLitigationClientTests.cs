using System.Net;
using System.Text;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers BprLitigationClient's confirmed-endpoint request shape (no "Bearer " prefix on
/// Authorization — confirmed from the vendor's own Postman collection example requests) and its defensive
/// handling of the two genuinely unconfirmed parts of the contract: the auth/register response field names,
/// and whether report/job/{id} is pending, complete or failed.</summary>
public sealed class BprLitigationClientTests
{
    private static readonly BprLitigationOptions Configured = new() { BaseUrl = "https://bpr.example/", Id = "app", SecretKey = "secret" };

    // A fake DNS map, never real network/DNS — IsSafeDestinationAsync's private-address check must stay
    // fully deterministic and CI-sandbox-safe. Addresses are from RFC 5737's documentation ranges (public-
    // looking but reserved for exactly this purpose) except where a test deliberately maps a host to a
    // private/loopback address to prove the block applies even to an otherwise-allowed hostname.
    private static readonly Dictionary<string, IPAddress[]> DefaultDnsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["bpr.example"] = [IPAddress.Parse("203.0.113.10")],
        ["cdn.approved.example"] = [IPAddress.Parse("203.0.113.20")],
        ["unapproved.example"] = [IPAddress.Parse("203.0.113.30")],
        ["internal-cdn.example"] = [IPAddress.Parse("127.0.0.1")]
    };

    private static Task<IPAddress[]> FakeResolver(string host, CancellationToken ct) =>
        DefaultDnsMap.TryGetValue(host, out var addresses)
            ? Task.FromResult(addresses)
            : Task.FromException<IPAddress[]>(new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.HostNotFound));

    private static BprLitigationClient NewClient(
        StubHandler handler, BprLitigationOptions? options = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolver = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://bpr.example/") },
        Options.Create(options ?? Configured),
        NullLogger<BprLitigationClient>.Instance,
        resolver ?? FakeResolver);

    // ── Authenticate ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AuthenticateAsync_sends_id_and_secret_key_and_extracts_the_jwt_field()
    {
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jwt":"token-value"}""", Encoding.UTF8, "application/json")
            };
        });
        var client = NewClient(handler);

        var token = await client.AuthenticateAsync(CancellationToken.None);

        Assert.Equal("token-value", token);
        var sentBody = handler.RequestBodies.Single();
        Assert.Contains("\"id\":\"app\"", sentBody);
        Assert.Contains("\"secret_key\":\"secret\"", sentBody);
    }

    [Theory]
    [InlineData("""{"token":"t2"}""")]
    [InlineData("""{"access_token":"t2"}""")]
    public async Task AuthenticateAsync_accepts_alternate_plausible_token_field_names(string body)
    {
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var token = await NewClient(handler).AuthenticateAsync(CancellationToken.None);

        Assert.Equal("t2", token);
    }

    [Fact]
    public async Task AuthenticateAsync_throws_a_clear_error_when_no_recognizable_token_field_is_present()
    {
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"unexpected_field":"x"}""", Encoding.UTF8, "application/json")
        });

        var ex = await Assert.ThrowsAsync<BprLitigationException>(() => NewClient(handler).AuthenticateAsync(CancellationToken.None));
        Assert.Contains("recognizable JSON token field", ex.Message);
    }

    [Fact]
    public async Task AuthenticateAsync_throws_when_not_configured()
    {
        var handler = new StubHandler();
        var client = NewClient(handler, new BprLitigationOptions());

        var ex = await Assert.ThrowsAsync<BprLitigationException>(() => client.AuthenticateAsync(CancellationToken.None));
        Assert.Contains("not configured", ex.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AuthenticateAsync_accepts_a_raw_JWT_body_confirmed_by_a_live_test_call()
    {
        // Regression for a review finding: a live test call to sec/authenticate returned the JWT as the
        // entire raw response body, not JSON-wrapped — the original implementation only tried JSON field
        // extraction and would have thrown on the real vendor response. A synthetic (not vendor-derived)
        // three-segment JWT-shaped string, used purely to exercise LooksLikeJwt's format check.
        const string rawJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.synthetic-test-payload-only.synthetic-test-signature-only";
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(rawJwt, Encoding.UTF8, "text/plain")
        });

        var token = await NewClient(handler).AuthenticateAsync(CancellationToken.None);

        Assert.Equal(rawJwt, token);
    }

    [Fact]
    public async Task AuthenticateAsync_accepts_a_quoted_raw_JWT_body()
    {
        const string rawJwt = "eyJhbGciOiJIUzI1NiJ9.eyJhcHBfaWQiOiJhcHAifQ.c2lnbmF0dXJl";
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"\"{rawJwt}\"", Encoding.UTF8, "application/json")
        });

        var token = await NewClient(handler).AuthenticateAsync(CancellationToken.None);

        Assert.Equal(rawJwt, token);
    }

    [Fact]
    public async Task AuthenticateAsync_still_prefers_a_JSON_wrapped_token_when_the_body_is_not_a_bare_JWT()
    {
        // A JSON object body is never mistaken for a raw JWT (LooksLikeJwt requires exactly 3 dot-separated
        // segments), so the JSON-field fallback still runs.
        var handler = new StubHandler();
        handler.OnPath("sec/authenticate", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"jwt":"wrapped-token"}""", Encoding.UTF8, "application/json")
        });

        var token = await NewClient(handler).AuthenticateAsync(CancellationToken.None);

        Assert.Equal("wrapped-token", token);
    }

    // ── Register ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegisterJobAsync_sends_the_raw_token_with_no_Bearer_prefix_and_extracts_job_id()
    {
        var handler = new StubHandler();
        handler.OnPath("bprjob/register", request =>
        {
            Assert.Equal("raw-token", request.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"job_id":"job-123"}""", Encoding.UTF8, "application/json")
            };
        });

        var jobId = await NewClient(handler).RegisterJobAsync(
            "raw-token", ["Bharat Petroleum Corporation Limited"], "individual", "req-1", CancellationToken.None);

        Assert.Equal("job-123", jobId);
        var sentBody = handler.RequestBodies.Single();
        Assert.Contains("\"entity_type\":\"individual\"", sentBody);
        Assert.Contains("\"application_customer_id\":\"req-1\"", sentBody);
    }

    [Fact]
    public async Task RegisterJobAsync_rejects_an_empty_keyword_list_without_calling_the_vendor()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            NewClient(handler).RegisterJobAsync("token", [], "individual", "req-1", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ── GetReport (the genuinely ambiguous part of the contract) ──────────────────────────────────

    [Fact]
    public async Task GetReportAsync_treats_a_ZIP_signature_body_as_a_completed_Xlsx_report()
    {
        var handler = new StubHandler();
        byte[] xlsxLike = [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02];
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(xlsxLike) });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Completed, result.Status);
        Assert.Equal(BprReportFormat.Xlsx, result.Format);
        Assert.Equal(xlsxLike, result.Bytes);
    }

    [Fact]
    public async Task GetReportAsync_treats_a_full_JSON_report_body_as_completed()
    {
        var handler = new StubHandler();
        var reportJson = """{"request_details":{"job_id":"job-1"},"high_court":[{"case_no":"12/2026","cnr_number":"XX"}]}""";
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(reportJson, Encoding.UTF8, "application/json")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Completed, result.Status);
        Assert.Equal(BprReportFormat.Json, result.Format);
    }

    [Fact]
    public async Task GetReportAsync_treats_the_confirmed_status_false_error_envelope_as_Failed_not_Completed()
    {
        // Regression for a review finding: a live test call against an unknown/not-found job returned HTTP
        // 200 with {"status":false,"message":"Job not found"} — valid, non-empty JSON that the original
        // implementation would have classified as a completed report (it has no string "status"/"state"
        // field for HasPendingStatusField to catch; "status" here is a JSON boolean, a different shape).
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":false,"message":"Job not found"}""", Encoding.UTF8, "application/json")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Failed, result.Status);
        Assert.Equal("Job not found", result.Message);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public async Task GetReportAsync_treats_a_status_false_envelope_with_no_message_as_Failed_with_a_generic_reason()
    {
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":false}""", Encoding.UTF8, "application/json")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Failed, result.Status);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task GetReportAsync_treats_a_status_true_boolean_envelope_as_Pending_not_Completed()
    {
        // "status:true" has no confirmed meaning, but it is a boolean control field, not report content
        // (a real report never carries a top-level boolean "status" — see BprLitigationReportParser) — must
        // not be stored as a completed report on the strength of an unconfirmed guess.
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":true}""", Encoding.UTF8, "application/json")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Pending, result.Status);
    }

    [Theory]
    [InlineData("""{"status":"processing"}""")]
    [InlineData("""{"state":"queued"}""")]
    public async Task GetReportAsync_treats_a_JSON_body_with_an_in_progress_status_field_as_pending(string body)
    {
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Pending, result.Status);
        Assert.Null(result.Bytes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task GetReportAsync_treats_not_ready_HTTP_statuses_as_pending(HttpStatusCode statusCode)
    {
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(statusCode));

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Pending, result.Status);
    }

    [Fact]
    public async Task GetReportAsync_treats_an_unexpected_error_status_as_failed_not_pending()
    {
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Failed, result.Status);
    }

    [Fact]
    public async Task GetReportAsync_never_throws_or_discards_an_unrecognized_200_body_marks_it_Unknown_instead()
    {
        var handler = new StubHandler();
        handler.OnPath("report/job/", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not json and not a zip", Encoding.UTF8, "text/plain")
        });

        var result = await NewClient(handler).GetReportAsync("token", "job-1", CancellationToken.None);

        Assert.Equal(BprReportPollStatus.Completed, result.Status);
        Assert.Equal(BprReportFormat.Unknown, result.Format);
        Assert.NotNull(result.Bytes);
    }

    // ── DownloadOrderDocumentAsync (#243/LIT-03 — not one of the three confirmed endpoints) ─────────

    private static readonly byte[] PdfBytes = [0x25, 0x50, 0x44, 0x46, 0x2D, 0x31, 0x2E, 0x34, 0x0A]; // "%PDF-1.4\n"

    [Fact]
    public async Task DownloadOrderDocumentAsync_returns_bytes_for_a_valid_PDF_response()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(PdfBytes) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") } }
        });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://bpr.example/orders/o1.pdf", 1024, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(PdfBytes, result.Bytes);
        Assert.Equal("application/pdf", result.ContentType);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_attaches_the_JWT_when_the_order_URL_shares_BPRs_host()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/o1.pdf", request =>
        {
            Assert.Equal("raw-token", request.Headers.GetValues("Authorization").Single());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) };
        });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "raw-token", "https://bpr.example/orders/o1.pdf", 1024, CancellationToken.None);

        Assert.True(result.Ok);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_never_attaches_the_JWT_to_an_approved_third_party_host()
    {
        // Regression: forwarding BPR's own JWT to an approved third-party document host (the confirmed
        // contract has no example of how order URLs are authorized) would leak the token into that host's
        // own access logs — must only ever be sent when the URL shares BPR's own configured host, even for a
        // host that passed the allowlist check.
        var handler = new StubHandler();
        handler.OnPath("files/o1.pdf", request =>
        {
            Assert.False(request.Headers.Contains("Authorization"));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) };
        });
        var options = new BprLitigationOptions
        {
            BaseUrl = Configured.BaseUrl, Id = Configured.Id, SecretKey = Configured.SecretKey,
            AllowedOrderDocumentHosts = ["cdn.approved.example"]
        };

        var result = await NewClient(handler, options).DownloadOrderDocumentAsync(
            "raw-token", "https://cdn.approved.example/files/o1.pdf", 1024, CancellationToken.None);

        Assert.True(result.Ok);
    }

    // ── SSRF prevention (PR #253 review): pdf_url is untrusted vendor-report content ─────────────────

    [Fact]
    public async Task DownloadOrderDocumentAsync_rejects_a_host_not_on_the_allowlist_without_calling_it()
    {
        var handler = new StubHandler();
        handler.OnPath("files/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://unapproved.example/files/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not BPR's own host or on the configured AllowedOrderDocumentHosts allowlist", result.Error);
        Assert.Empty(handler.Requests); // the server-side request is never made
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_rejects_a_plain_HTTP_url_even_on_an_allowed_host()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "http://bpr.example/orders/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("not a valid absolute HTTPS URL", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_rejects_a_private_IP_literal_even_when_explicitly_allowlisted()
    {
        // Defense in depth: even a misconfigured allowlist entry that is itself a raw metadata/private IP
        // address must still be refused — the private-address check is not something the allowlist can
        // override.
        var handler = new StubHandler();
        handler.OnPath("latest/meta-data", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) });
        var options = new BprLitigationOptions
        {
            BaseUrl = Configured.BaseUrl, Id = Configured.Id, SecretKey = Configured.SecretKey,
            AllowedOrderDocumentHosts = ["169.254.169.254"]
        };

        var result = await NewClient(handler, options).DownloadOrderDocumentAsync(
            "token", "https://169.254.169.254/latest/meta-data", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("resolves to a private, loopback or link-local address", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_rejects_an_allowlisted_hostname_that_resolves_to_a_private_address()
    {
        // The allowlist check alone is not enough — a hostname (unlike a literal IP) could resolve
        // internally now or after a future DNS change, so the destination is always resolved and checked.
        var handler = new StubHandler();
        handler.OnPath("files/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(PdfBytes) });
        var options = new BprLitigationOptions
        {
            BaseUrl = Configured.BaseUrl, Id = Configured.Id, SecretKey = Configured.SecretKey,
            AllowedOrderDocumentHosts = ["internal-cdn.example"] // maps to 127.0.0.1 in DefaultDnsMap
        };

        var result = await NewClient(handler, options).DownloadOrderDocumentAsync(
            "token", "https://internal-cdn.example/files/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("resolves to a private, loopback or link-local address", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_fails_closed_when_the_host_cannot_be_resolved_at_all()
    {
        var options = new BprLitigationOptions
        {
            BaseUrl = Configured.BaseUrl, Id = Configured.Id, SecretKey = Configured.SecretKey,
            AllowedOrderDocumentHosts = ["nonexistent-host.example"] // not in DefaultDnsMap — resolver throws
        };
        var handler = new StubHandler();

        var result = await NewClient(handler, options).DownloadOrderDocumentAsync(
            "token", "https://nonexistent-host.example/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(handler.Requests);
    }

    // ── CreateSafeConnectCallback (PR #253 review round 2 — DNS rebinding) ────────────────────────────

    // SocketsHttpConnectionContext has no public constructor, so the callback can only be exercised by
    // actually triggering SocketsHttpHandler's own connection pipeline — a real HttpClient request against a
    // handler configured with the callback under test. The callback fully replaces DNS resolution AND
    // connection establishment, so the target host name itself never needs to be real; only the fake
    // resolver's return value matters.

    [Fact]
    public async Task CreateSafeConnectCallback_refuses_to_connect_when_the_resolved_address_is_private()
    {
        // The DNS-rebinding fix itself: IsSafeDestinationAsync resolves and validates once, up front, but
        // SocketsHttpHandler resolves AGAIN, independently, when it actually opens the connection — if the
        // DNS record changed in between, that second resolution is what the request really reaches. This
        // proves the connect-time resolution is validated too, using its own fresh result.
        using var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = BprLitigationClient.CreateSafeConnectCallback((_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("127.0.0.1")]))
        };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://rebound.example/"));

        Assert.Contains("private, loopback or link-local", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task CreateSafeConnectCallback_refuses_when_any_of_several_resolved_addresses_is_private()
    {
        using var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = BprLitigationClient.CreateSafeConnectCallback(
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.60"), IPAddress.Parse("10.0.0.5")]))
        };
        using var client = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("https://multi.example/"));

        Assert.Contains("private, loopback or link-local", ex.InnerException?.Message ?? ex.Message);
    }

    [Fact]
    public async Task CreateSafeConnectCallback_resolves_the_host_exactly_once_never_a_separate_resolution_to_connect_with()
    {
        // The property that actually closes the rebinding window: one resolution feeds both the validation
        // AND the connection attempt — there is no second, independent resolve() call in between for a DNS
        // record to change during.
        var callCount = 0;
        using var handler = new System.Net.Http.SocketsHttpHandler
        {
            ConnectCallback = BprLitigationClient.CreateSafeConnectCallback((_, _) =>
            {
                Interlocked.Increment(ref callCount);
                // TEST-NET-3 (RFC 5737): public-looking, reserved for documentation, never actually
                // routable — the connect attempt itself is expected to fail/time out; only the resolver
                // call count matters.
                return Task.FromResult<IPAddress[]>([IPAddress.Parse("203.0.113.50")]);
            }),
            ConnectTimeout = TimeSpan.FromSeconds(2)
        };
        using var client = new HttpClient(handler);

        await Assert.ThrowsAnyAsync<Exception>(() => client.GetAsync("https://rebound.example:65530/"));

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task BprLitigationHttpClient_registration_disables_automatic_redirect_following()
    {
        // Regression for PR #253 review: the host-allowlist/private-address checks above only ever see the
        // URL a vendor report asserts. If the underlying transport transparently followed a redirect, a
        // response could silently come from a completely different, unchecked host — the checks would have
        // been bypassed, not merely skipped. This proves the exact handler configuration
        // Program.cs's AddHttpClient<BprLitigationClient> registration uses actually prevents that (keep
        // this in sync with that registration if it ever changes).
        using var listener = new HttpListener();
        var port = GetFreeTcpPort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serverTask = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "http://internal.example/should-never-be-fetched";
            ctx.Response.Close();
        });

        using var handler = new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);

        var response = await client.GetAsync($"http://127.0.0.1:{port}/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // never silently followed to the Location host
        listener.Stop();
        await serverTask;
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_fails_on_a_non_PDF_signature_response()
    {
        // A dead/expired link commonly returns an HTML error page or a JSON error body with HTTP 200 — must
        // never be stored and reported as a retrieved order.
        var handler = new StubHandler();
        handler.OnPath("orders/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Not Found</body></html>", Encoding.UTF8, "text/html")
        });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://bpr.example/orders/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("PDF signature", result.Error);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_fails_when_declared_ContentLength_exceeds_the_cap()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/big.pdf", _ =>
        {
            var content = new ByteArrayContent(PdfBytes);
            content.Headers.ContentLength = 10_000_000;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://bpr.example/orders/big.pdf", maxBytes: 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("exceeding the 1,024-byte cap", result.Error);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_fails_when_the_actual_stream_exceeds_the_cap_with_no_declared_length()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/big.pdf", _ =>
        {
            var oversized = new byte[2048];
            Array.Copy(PdfBytes, oversized, PdfBytes.Length);
            var content = new StreamContent(new MemoryStream(oversized));
            // A seekable MemoryStream otherwise lets StreamContent auto-report Content-Length from
            // stream.Length — forcing it null exercises the bounded-read path rather than the
            // declared-length short-circuit above (a chunked/non-seekable real response has no such header).
            content.Headers.ContentLength = null;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://bpr.example/orders/big.pdf", maxBytes: 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("exceeded the 1,024-byte cap", result.Error);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_fails_on_a_non_success_HTTP_status()
    {
        var handler = new StubHandler();
        handler.OnPath("orders/o1.pdf", _ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "https://bpr.example/orders/o1.pdf", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("403", result.Error);
    }

    [Fact]
    public async Task DownloadOrderDocumentAsync_rejects_a_non_absolute_or_non_http_url_without_calling_anything()
    {
        var handler = new StubHandler();

        var result = await NewClient(handler).DownloadOrderDocumentAsync(
            "token", "not-a-url", 1024, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Empty(handler.Requests);
    }

    // ── Stub infrastructure — mirrors ReferenceToolClientTests' route-based StubHandler ───────────

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly List<(string PathSuffix, Func<HttpRequestMessage, HttpResponseMessage> Respond)> _routes = [];
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> RequestBodies { get; } = [];
        public void OnPath(string pathSuffix, Func<HttpRequestMessage, HttpResponseMessage> respond) => _routes.Add((pathSuffix, respond));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            // Captured now, synchronously with the send — the caller disposes the request (and its
            // content) right after SendAsync returns, so reading it later would throw ObjectDisposedException.
            RequestBodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(ct));

            var route = _routes.FirstOrDefault(r => request.RequestUri!.AbsolutePath.Contains(r.PathSuffix, StringComparison.Ordinal));
            return route.Respond is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("no stub for " + request.RequestUri) }
                : route.Respond(request);
        }
    }
}
