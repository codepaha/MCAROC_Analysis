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

    private static BprLitigationClient NewClient(StubHandler handler, BprLitigationOptions? options = null) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://bpr.example/") },
        Options.Create(options ?? Configured),
        NullLogger<BprLitigationClient>.Instance);

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
