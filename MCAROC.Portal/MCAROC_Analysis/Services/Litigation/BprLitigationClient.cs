using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Thin client for the three confirmed BPR Litigation Data API endpoints (authenticate, register,
/// report — see the vendor's own Postman collection, never committed to source). Two parts of the contract
/// are still unconfirmed and are handled defensively rather than assumed:
/// <list type="bullet">
/// <item>The authenticate/register response field names for the token and vendor job id — no example
/// response was captured for either call. <see cref="ExtractToken"/>/<see cref="ExtractJobId"/> try a short
/// list of plausible field names and fail loudly, naming the actual top-level keys received (never the
/// values), if none match.</item>
/// <item>Whether GET report/job/{id} distinguishes "still processing" from "complete" via HTTP status, a
/// JSON status field, or content type — the vendor's own captured example returned a raw XLSX binary for a
/// job registered with file_format=JSON, a confirmed discrepancy. <see cref="GetReportAsync"/> sniffs the
/// actual response rather than trusting configuration.</item>
/// </list>
/// Both gaps are documented in docs/litigation-data-lake-integration.md's "Vendor contract required"
/// section and must be revisited once a real account confirms the exact shapes.</summary>
public sealed class BprLitigationClient(HttpClient http, IOptions<BprLitigationOptions> options, ILogger<BprLitigationClient> logger)
{
    private static readonly string[] TokenFieldCandidates = ["jwt", "token", "access_token", "Authorization", "authorization"];
    private static readonly string[] JobIdFieldCandidates = ["job_id", "jobId", "id", "request_id", "requestId"];
    private static readonly string[] PendingStatusValues = ["pending", "processing", "in_progress", "inprogress", "queued", "running"];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04]; // "PK\x03\x04" — XLSX is a zip container

    private readonly BprLitigationOptions _opts = options.Value;

    /// <summary>POST sec/authenticate with the configured id/secret_key. Returns the raw token exactly as
    /// received — it is sent back verbatim as the Authorization header value on later calls, with no
    /// "Bearer " prefix (confirmed from the vendor's own example requests). Never logged.</summary>
    public async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        RequireConfigured();
        using var response = await http.PostAsJsonAsync("sec/authenticate", new { id = _opts.Id, secret_key = _opts.SecretKey }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BprLitigationException($"BPR authentication failed with HTTP {(int)response.StatusCode}.");

        var token = ExtractStringField(body, TokenFieldCandidates);
        if (token is null)
            throw new BprLitigationException(
                "BPR authentication succeeded but the response did not contain a recognizable token field " +
                "(tried: " + string.Join(", ", TokenFieldCandidates) + "). The vendor's response schema for " +
                "this call was never confirmed — see docs/litigation-data-lake-integration.md.");
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
            if (root.ValueKind == JsonValueKind.Object && HasPendingStatusField(root))
                return BprReportPollResult.Pending("Report response carries an in-progress status field.");
            return BprReportPollResult.Completed(bytes, BprReportFormat.Json);
        }

        logger.LogWarning(
            "BPR report/job/{VendorJobId} returned a 200 response that is neither a ZIP/XLSX signature nor valid JSON " +
            "({ByteCount} bytes, Content-Type {ContentType}) — storing as Unknown for manual inspection.",
            vendorJobId, bytes.Length, contentType ?? "(none)");
        return BprReportPollResult.Completed(bytes, BprReportFormat.Unknown);
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
