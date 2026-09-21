using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Thin client for the three confirmed BPR Litigation Data API endpoints (authenticate, register,
/// report — see the vendor's own Postman collection, never committed to source).
/// <list type="bullet">
/// <item><b>Confirmed by a live test call:</b> POST sec/authenticate returns the JWT as a raw response body
/// (not JSON-wrapped) — <see cref="AuthenticateAsync"/> accepts that directly, with a JSON-field fallback
/// kept only in case the vendor ever wraps it.</item>
/// <item>The register response field name for the vendor job id is still unconfirmed — no example response
/// was captured for that call. <see cref="ExtractStringField"/> tries a short list of plausible field names
/// and fails loudly, naming the actual top-level keys received (never the values), if none match.</item>
/// <item>GET report/job/{id} has no separate status/polling endpoint, so one response must distinguish
/// "still processing" from "complete" from "failed" itself — see <see cref="GetReportAsync"/>. Confirmed by
/// a live test call: a not-found job returns HTTP 200 with a JSON error envelope
/// (<c>{"status":false,"message":"..."}</c>) — a valid, non-empty JSON body that is neither a report nor a
/// string pending-status field, so it is classified explicitly rather than falling through to Completed.</item>
/// </list></summary>
public sealed class BprLitigationClient(
    HttpClient http, IOptions<BprLitigationOptions> options, ILogger<BprLitigationClient> logger,
    Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null)
{
    private static readonly string[] TokenFieldCandidates = ["jwt", "token", "access_token", "Authorization", "authorization"];
    private static readonly string[] JobIdFieldCandidates = ["job_id", "jobId", "id", "request_id", "requestId"];
    private static readonly string[] PendingStatusValues = ["pending", "processing", "in_progress", "inprogress", "queued", "running"];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04]; // "PK\x03\x04" — XLSX is a zip container
    private static readonly byte[] PdfSignature = [0x25, 0x50, 0x44, 0x46, 0x2D]; // "%PDF-"

    private readonly BprLitigationOptions _opts = options.Value;
    // Defaults to real DNS resolution in production; tests inject a canned resolver so
    // IsSafeDestinationAsync's private-address check is deterministic and never depends on real network/DNS
    // reachability (which a CI sandbox may not have for arbitrary hostnames at all).
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost = hostResolver ?? Dns.GetHostAddressesAsync;

    /// <summary>POST sec/authenticate with the configured id/secret_key. A live test call confirmed the
    /// response body is the raw JWT itself, not JSON — that is tried first; a JSON-wrapped token (one of
    /// <see cref="TokenFieldCandidates"/>) is accepted as a fallback in case the vendor changes this. Returns
    /// the token exactly as received — sent back verbatim as the Authorization header value on later calls,
    /// with no "Bearer " prefix (also confirmed from the vendor's own example requests). Never logged.</summary>
    public async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        RequireConfigured();
        using var response = await http.PostAsJsonAsync("sec/authenticate", new { id = _opts.Id, secret_key = _opts.SecretKey }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BprLitigationException($"BPR authentication failed with HTTP {(int)response.StatusCode}.");

        var token = ExtractRawJwt(body) ?? ExtractStringField(body, TokenFieldCandidates);
        if (token is null)
            throw new BprLitigationException(
                "BPR authentication succeeded but the response was neither a raw JWT body nor a recognizable " +
                "JSON token field (tried: " + string.Join(", ", TokenFieldCandidates) + ").");
        return token;
    }

    /// <summary>POST bprjob/register with only approved keywords (the caller is responsible for building
    /// the plan via <see cref="LitigationKeywordPlanner"/> — this client never invents search terms).
    /// Returns the vendor's job id. Callers must not call this twice for the same logical search; job-level
    /// idempotency (skip registration once a vendor job id is already recorded) is the caller's
    /// responsibility, not this client's — it has no state of its own.</summary>
    public async Task<string> RegisterJobAsync(
        string token, IReadOnlyList<string> keywords, string entityType, string applicationCustomerId, CancellationToken ct)
    {
        RequireConfigured();
        if (keywords.Count == 0) throw new ArgumentException("At least one approved keyword is required.", nameof(keywords));

        using var request = new HttpRequestMessage(HttpMethod.Post, "bprjob/register");
        request.Headers.TryAddWithoutValidation("Authorization", token);
        request.Content = JsonContent.Create(new
        {
            entity_type = entityType,
            keywords,
            application_customer_id = applicationCustomerId,
            file_format = _opts.FileFormat,
            exact_match = _opts.ExactMatch,
            formats = _opts.Formats
        });

        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BprLitigationException($"BPR job registration failed with HTTP {(int)response.StatusCode}.");

        var jobId = ExtractStringField(body, JobIdFieldCandidates);
        if (jobId is null)
            throw new BprLitigationException(
                "BPR job registration succeeded but the response did not contain a recognizable job id field " +
                "(tried: " + string.Join(", ", JobIdFieldCandidates) + ").");
        return jobId;
    }

    /// <summary>GET report/job/{vendorJobId}. There is no separate status/polling endpoint in the confirmed
    /// contract, so this single call must distinguish "not ready yet" from "complete" from "failed" itself:
    /// <list type="bullet">
    /// <item>HTTP 202/204/404/425 → <see cref="BprReportPollStatus.Pending"/> (report-generation lag is the
    /// documented reason a fresh job id might briefly 404).</item>
    /// <item>HTTP 200 with a small JSON body carrying a recognizable in-progress status field →
    /// <see cref="BprReportPollStatus.Pending"/>.</item>
    /// <item>HTTP 200 whose bytes start with the ZIP signature (XLSX) or whose Content-Type says so →
    /// <see cref="BprReportPollStatus.Completed"/> with <see cref="BprReportFormat.Xlsx"/>.</item>
    /// <item>HTTP 200 with any other non-trivial body → <see cref="BprReportPollStatus.Completed"/> with
    /// <see cref="BprReportFormat.Json"/> if it parses as JSON, else <see cref="BprReportFormat.Unknown"/> —
    /// never discarded, always handed back for the caller to store and a human to inspect.</item>
    /// <item>Any other HTTP status → <see cref="BprReportPollStatus.Failed"/>.</item>
    /// </list></summary>
    public async Task<BprReportPollResult> GetReportAsync(string token, string vendorJobId, CancellationToken ct)
    {
        RequireConfigured();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"report/job/{Uri.EscapeDataString(vendorJobId)}");
        request.Headers.TryAddWithoutValidation("Authorization", token);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (response.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.NoContent
            or HttpStatusCode.NotFound or (HttpStatusCode)425)
            return BprReportPollResult.Pending($"HTTP {(int)response.StatusCode} — treated as report not ready yet.");

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
            return BprReportPollResult.Failed($"HTTP {(int)response.StatusCode} from report/job/{vendorJobId}.");

        if (bytes.Length == 0)
            return BprReportPollResult.Pending("HTTP 200 with an empty body — treated as report not ready yet.");

        if (StartsWithZipSignature(bytes))
            return BprReportPollResult.Completed(bytes, BprReportFormat.Xlsx);

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && contentType.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase))
            return BprReportPollResult.Completed(bytes, BprReportFormat.Xlsx);

        var text = System.Text.Encoding.UTF8.GetString(bytes);
        if (TryParseJson(text, out var root))
        {
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryClassifyControlEnvelope(root, out var controlResult))
                    return controlResult!;
                if (HasPendingStatusField(root))
                    return BprReportPollResult.Pending("Report response carries an in-progress status field.");
            }
            return BprReportPollResult.Completed(bytes, BprReportFormat.Json);
        }

        logger.LogWarning(
            "BPR report/job/{VendorJobId} returned a 200 response that is neither a ZIP/XLSX signature nor valid JSON " +
            "({ByteCount} bytes, Content-Type {ContentType}) — storing as Unknown for manual inspection.",
            vendorJobId, bytes.Length, contentType ?? "(none)");
        return BprReportPollResult.Completed(bytes, BprReportFormat.Unknown);
    }

    /// <summary>Downloads one order's PDF from <paramref name="pdfUrl"/> (a <c>LitigationCaseOrder.PdfUrl</c>
    /// value, captured verbatim from a BPR report — untrusted vendor-report content, not one of the three
    /// confirmed BPR endpoints, with no captured example of how an order URL is authorized). Several
    /// defensive checks follow from that, in order:
    /// <list type="bullet">
    /// <item><b>HTTPS only, and only to an explicitly allowed host</b> (<see cref="BprLitigationOptions.AllowedOrderDocumentHosts"/>
    /// plus BPR's own configured host) — never a URL the vendor report merely asserts. Without this, a
    /// malicious or compromised report could point <c>pdf_url</c> at an arbitrary internal service or cloud
    /// metadata endpoint and have this server fetch (and, if the response starts with the PDF signature,
    /// retain) it — a server-side request forgery path. This check runs before any network call.</item>
    /// <item><b>The allowed host must not resolve to a private/loopback/link-local address</b> — checked via
    /// DNS resolution of the actual destination, not the literal hostname string, so an allowed hostname that
    /// (now or via a future DNS change) resolves internally is still refused. Defense in depth even for an
    /// intentionally-allowlisted host.</item>
    /// <item><b>Redirects are never followed</b> — the caller's <see cref="HttpClient"/> is registered with
    /// automatic redirects disabled (see <c>Program.cs</c>'s <c>AddHttpClient&lt;BprLitigationClient&gt;</c>
    /// registration) specifically so a 3xx response can never silently pivot this request to a host the
    /// checks above never saw. A 3xx here is simply a failed download, like any other non-2xx status.</item>
    /// <item>The JWT is only ever attached when <paramref name="pdfUrl"/> shares BPR's own configured host —
    /// never forwarded to an approved third-party document host, which would leak the token into that host's
    /// own access logs. If the URL is a self-contained pre-signed link (the common pattern for a time-limited
    /// document link, and consistent with epic #239's "may expire after seven days" reading as a signed-URL
    /// TTL), it needs no Authorization header at all.</item>
    /// <item>The response is read as a bounded stream, never buffered unbounded into memory — the vendor
    /// contract documents no file-size limit, so a declared or actual size over <paramref name="maxBytes"/>
    /// is treated as a failed download rather than risking unbounded memory use for one order.</item>
    /// </list>
    /// Validates the downloaded bytes actually start with the PDF signature before calling it a success — a
    /// dead/expired link commonly returns an HTML error page or a JSON error body with HTTP 200, which must
    /// never be stored and reported as a retrieved order.</summary>
    public async Task<BprOrderDownloadResult> DownloadOrderDocumentAsync(string token, string pdfUrl, long maxBytes, CancellationToken ct)
    {
        RequireConfigured();
        if (!Uri.TryCreate(pdfUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return BprOrderDownloadResult.Failed($"Order PDF URL is not a valid absolute HTTPS URL: '{pdfUrl}'.");

        var bprHost = http.BaseAddress?.Host;
        var isBprHost = bprHost is not null && string.Equals(uri.Host, bprHost, StringComparison.OrdinalIgnoreCase);
        if (!isBprHost && !_opts.AllowedOrderDocumentHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
            return BprOrderDownloadResult.Failed(
                $"Order PDF host '{uri.Host}' is not BPR's own host or on the configured AllowedOrderDocumentHosts allowlist — refusing to fetch it.");

        if (!await IsSafeDestinationAsync(uri.Host, ct))
            return BprOrderDownloadResult.Failed(
                $"Order PDF host '{uri.Host}' resolves to a private, loopback or link-local address — refusing to fetch it.");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (isBprHost)
            request.Headers.TryAddWithoutValidation("Authorization", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            return BprOrderDownloadResult.Failed($"Network error downloading order PDF: {ex.Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return BprOrderDownloadResult.Failed($"HTTP {(int)response.StatusCode} downloading order PDF.");

            var declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is { } length && length > maxBytes)
                return BprOrderDownloadResult.Failed(
                    $"Order PDF declares {length:N0} bytes, exceeding the {maxBytes:N0}-byte cap.");

            byte[] bytes;
            try
            {
                bytes = await ReadBoundedAsync(response.Content, maxBytes, ct);
            }
            catch (BprLitigationException ex)
            {
                return BprOrderDownloadResult.Failed(ex.Message);
            }

            if (bytes.Length == 0)
                return BprOrderDownloadResult.Failed("Order PDF download returned an empty body.");
            if (!StartsWithPdfSignature(bytes))
                return BprOrderDownloadResult.Failed(
                    $"Downloaded content ({bytes.Length:N0} bytes) does not start with the PDF signature — " +
                    "likely an error page or an expired link, not the order.");

            return BprOrderDownloadResult.Success(bytes, response.Content.Headers.ContentType?.MediaType);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new BprLitigationException($"Order PDF exceeded the {maxBytes:N0}-byte cap while downloading.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static bool StartsWithPdfSignature(byte[] bytes) =>
        bytes.Length >= PdfSignature.Length && bytes.AsSpan(0, PdfSignature.Length).SequenceEqual(PdfSignature);

    // Private/loopback/link-local ranges an order PDF host must never resolve to, even if the hostname
    // itself is on the allowlist — defense in depth against DNS pointing an approved-looking name at an
    // internal address (now, or via a future DNS change).
    private static readonly IReadOnlyList<IPNetwork> BlockedDestinationNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), // link-local — includes cloud metadata endpoints (e.g. 169.254.169.254)
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"), // carrier-grade NAT — internal-facing in practice
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fc00::/7")
    ];

    private static bool IsBlockedAddress(IPAddress address)
    {
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return BlockedDestinationNetworks.Any(network => network.Contains(normalized));
    }

    /// <summary>Resolves <paramref name="host"/> and checks every returned address against
    /// <see cref="BlockedDestinationNetworks"/> — resolving rather than pattern-matching the hostname string,
    /// so this catches an allowlisted host that (now or later) resolves to an internal address, not just an
    /// obviously-internal literal. A resolution failure or an empty result is treated as unsafe (fail closed)
    /// rather than silently proceeding.
    ///
    /// This is a fast-fail check only, for a clear error message before ever opening a socket — it is NOT
    /// the authoritative enforcement. <see cref="CreateSafeConnectCallback"/> is: this method's own
    /// resolution happens here, but <see cref="SocketsHttpHandler"/> resolves the hostname AGAIN, independently,
    /// when it actually opens the connection — a classic DNS-rebinding window (the record changes between
    /// the two resolutions) that this check alone cannot close. See that method's remarks.</summary>
    private async Task<bool> IsSafeDestinationAsync(string host, CancellationToken ct)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await _resolveHost(host, ct);
            }
            catch (Exception)
            {
                return false;
            }
        }

        if (addresses.Length == 0) return false;
        return addresses.All(address => !IsBlockedAddress(address));
    }

    /// <summary>Builds a <see cref="SocketsHttpHandler.ConnectCallback"/> that resolves the destination host
    /// and opens the TCP connection to a specific, just-validated address itself, instead of letting
    /// <see cref="SocketsHttpHandler"/> do its own independent resolve-then-connect. This closes a DNS-
    /// rebinding gap <see cref="IsSafeDestinationAsync"/> alone cannot: that method resolves and validates the
    /// host once, up front; without this callback, the actual connection later triggers a SECOND, entirely
    /// independent resolution, and if the DNS record changes in between (trivial for an attacker who controls
    /// DNS for an allowlisted host), the validated check and the real destination can disagree — the request
    /// would reach whatever the second resolution returned, private address or not. Resolving and validating
    /// immediately before connecting, using the exact address then connected to, leaves no such window.
    /// Applied uniformly to every request this <see cref="HttpClient"/> makes (not just order-PDF downloads):
    /// BPR's own confirmed endpoints target an operator-configured <c>BaseUrl</c>, never vendor-report
    /// content, so this never blocks legitimate traffic to them unless BPR is ever deployed on a private
    /// network — unconfirmed and unsupported by anything in the current contract; revisit here if that
    /// changes. The original hostname is preserved for the caller's own Host header / TLS SNI — only the
    /// physical TCP connection target changes.
    ///
    /// <b>Known, deliberately accepted gap (risk-accepted by the owner on 2026-09-21, PR #253):</b> this
    /// protection only applies to a DIRECT connection. If a system/environment proxy is active (the default
    /// <see cref="SocketsHttpHandler.UseProxy"/> behavior, left as-is here), this callback connects to the
    /// PROXY's address, not the order host's — the proxy performs the real DNS resolution and connection on
    /// this application's behalf, entirely outside this check's visibility, reopening the same rebinding
    /// class of gap one layer further out (and a private-IP corporate proxy would itself be refused by the
    /// address check above, breaking legitimate proxy use). Disabling the outer allowlist/HTTPS-
    /// only/no-redirect/host-scoped-JWT protections would be the real regression; those remain in force
    /// regardless of proxy use and are judged sufficient for this internal, fixed-vendor integration's actual
    /// threat model. Full proxy-aware destination validation is deferred, tracked here rather than in a
    /// separate issue — revisit if this server is ever exposed to less-trusted input or gains reachability to
    /// more sensitive internal services.</summary>
    public static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>> CreateSafeConnectCallback(
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null)
    {
        var resolveHost = hostResolver ?? Dns.GetHostAddressesAsync;
        return async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            IPAddress[] addresses;
            if (IPAddress.TryParse(host, out var literal))
            {
                addresses = [literal];
            }
            else
            {
                addresses = await resolveHost(host, ct);
            }

            if (addresses.Length == 0)
                throw new InvalidOperationException($"Could not resolve '{host}' to connect.");

            foreach (var address in addresses)
            {
                if (IsBlockedAddress(address))
                    throw new InvalidOperationException(
                        $"Refusing to connect to '{host}' — resolved to a private, loopback or link-local address ({address}).");
            }

            var target = addresses[0].IsIPv4MappedToIPv6 ? addresses[0].MapToIPv4() : addresses[0];
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    private void RequireConfigured()
    {
        if (!_opts.IsConfigured)
            throw new BprLitigationException(
                "BPR litigation client is not configured: set BprLitigation:BaseUrl, BprLitigation:Id and " +
                "BprLitigation:SecretKey (user-secrets/environment only — never a committed file).");
    }

    private static bool StartsWithZipSignature(byte[] bytes) =>
        bytes.Length >= ZipSignature.Length && bytes.AsSpan(0, ZipSignature.Length).SequenceEqual(ZipSignature);

    private static bool TryParseJson(string text, out JsonElement root)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static bool HasPendingStatusField(JsonElement root)
    {
        foreach (var name in new[] { "status", "state", "job_status" })
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
                Array.IndexOf(PendingStatusValues, value.GetString()?.ToLowerInvariant()) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Recognizes the confirmed BPR error-envelope shape — a JSON <b>boolean</b> <c>status</c> field
    /// (e.g. <c>{"status":false,"message":"Job not found"}</c>), as opposed to the <b>string</b> status field
    /// <see cref="HasPendingStatusField"/> checks for. A genuine report never carries a top-level boolean
    /// "status" (see <see cref="BprLitigationReportParser"/> — real reports have "request_details" plus
    /// nested court-category keys, never this), so any boolean status field means this 200 response is a
    /// control envelope, not report content, and must never fall through to being stored as a completed
    /// report. <c>status:false</c> is a confirmed failure (uses "message" if present). <c>status:true</c> has
    /// no confirmed meaning here, so it is treated as not-yet-a-report (Pending) rather than risk storing a
    /// non-report payload as Completed — the safe direction to err in.</summary>
    private static bool TryClassifyControlEnvelope(JsonElement root, out BprReportPollResult? result)
    {
        result = null;
        if (!root.TryGetProperty("status", out var statusValue) || statusValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        if (statusValue.ValueKind == JsonValueKind.False)
        {
            var message = root.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
                ? messageValue.GetString()
                : null;
            result = BprReportPollResult.Failed(message ?? "BPR returned a status:false error envelope with no message.");
            return true;
        }

        result = BprReportPollResult.Pending("BPR returned a status:true acknowledgement envelope — not yet a report.");
        return true;
    }

    private static string? ExtractStringField(string json, string[] candidates)
    {
        if (!TryParseJson(json, out var root) || root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in candidates)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
        }
        return null;
    }

    /// <summary>Accepts the confirmed real shape of a sec/authenticate response: the JWT as the entire raw
    /// body. Also tolerates a body wrapped in one extra layer of JSON-string quoting (<c>"eyJ..."</c>), in
    /// case a proxy or client library re-serializes it.</summary>
    private static string? ExtractRawJwt(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
            trimmed = trimmed[1..^1];
        return LooksLikeJwt(trimmed) ? trimmed : null;
    }

    /// <summary>A JWT is three base64url segments separated by dots (header.payload.signature) — a loose but
    /// specific enough check to distinguish a real token from an unrelated raw response body.</summary>
    private static bool LooksLikeJwt(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && Array.TrueForAll(parts, part => part.Length > 0 && IsBase64UrlSegment(part));
    }

    private static bool IsBase64UrlSegment(string segment)
    {
        foreach (var c in segment)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))
                return false;
        }
        return true;
    }
}

public enum BprReportFormat { Unknown, Json, Xlsx }

public enum BprReportPollStatus { Pending, Completed, Failed }

public sealed record BprReportPollResult(BprReportPollStatus Status, byte[]? Bytes, BprReportFormat Format, string? Message)
{
    public static BprReportPollResult Pending(string message) => new(BprReportPollStatus.Pending, null, BprReportFormat.Unknown, message);
    public static BprReportPollResult Completed(byte[] bytes, BprReportFormat format) => new(BprReportPollStatus.Completed, bytes, format, null);
    public static BprReportPollResult Failed(string message) => new(BprReportPollStatus.Failed, null, BprReportFormat.Unknown, message);
}

/// <summary>Raised for any BPR API failure — HTTP-level, misconfiguration, or an unrecognized response
/// shape. Never carries the token/secret; callers may log this exception's message safely.</summary>
public sealed class BprLitigationException(string message) : Exception(message);

public sealed record BprOrderDownloadResult(bool Ok, byte[]? Bytes, string? ContentType, string? Error)
{
    public static BprOrderDownloadResult Success(byte[] bytes, string? contentType) => new(true, bytes, contentType, null);
    public static BprOrderDownloadResult Failed(string error) => new(false, null, null, error);
}
