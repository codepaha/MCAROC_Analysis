using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Raw HTTP access to the reference tool: request signing, the company search, the two
/// workbook exports, the reference-documents registry and per-file PDF streaming. Knows nothing about
/// requests or jobs — <see cref="AutoFetchJobService"/> sequences these calls.
///
/// Protocol notes (all observed from the tool's own front end, none documented by the vendor):
/// <list type="bullet">
/// <item>Every JSON call is a GET whose <c>qp</c> query parameter is an HS256 JWT of the action payload.
/// The signing key is served by an unauthenticated bootstrap endpoint as a hex string.</item>
/// <item>Authentication is purely cookie-based (<see cref="ReferenceToolOptions.SessionCookie"/>).</item>
/// <item>The workbook export and the PDF download are plain query-string GETs that return the binary
/// directly; a company that is not unlocked, or an expired session, comes back as JSON/HTML with HTTP 200,
/// so every binary response is checked by its file signature, never by status code alone.</item>
/// <item>The PDF endpoint has no archive size cap, which is why filings are fetched one file at a time
/// instead of asking the tool to build a zip (that path fails past 50 MB).</item>
/// </list></summary>
public sealed class ReferenceToolClient(HttpClient http, IOptions<ReferenceToolOptions> options, ILogger<ReferenceToolClient> logger)
{
    private static readonly byte[] OleSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] ZipSignature = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] PdfSignature = "%PDF"u8.ToArray();
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>The registry's four sections, in the order the tool's UI shows them, with the outer-folder
    /// names the filings archive uses (FilingClassifier's folder fallback keys on "charge" / "incorporation").</summary>
    public static readonly IReadOnlyList<(string Key, string FolderName)> RegistrySections =
    [
        ("annual", "Financial Documents"),
        ("charge", "Charge Documents"),
        ("incorporation", "Incorporation Documents"),
        ("scanned", "Scanned Documents"),
    ];

    private readonly ReferenceToolOptions _opts = options.Value;
    private byte[]? _signingKey;
    private readonly SemaphoreSlim _keyGate = new(1, 1);

    public bool IsConfigured => _opts.IsConfigured;

    /// <summary>The tool identifies a company by <c>sha256(upper(CIN))</c> — deterministic, so no
    /// lookup is needed to go from a CIN/LLPIN to its business id.</summary>
    public static string ComputeBid(string cin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(cin.Trim().ToUpperInvariant()))).ToLowerInvariant();

    // ── Session ────────────────────────────────────────────────────────────────────────────────────

    public async Task<ReferenceSessionInfo> CheckSessionAsync(CancellationToken ct)
    {
        EnsureConfigured();
        using var response = await SendSignedAsync("server/user/userDetailsService.php", new { action = "getUserDetails" }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new ReferenceSessionInfo(false, null, $"HTTP {(int)response.StatusCode}");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new ReferenceSessionInfo(false, null, "non-JSON response (logged out?)"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ReferenceSessionInfo(false, null, "unexpected response shape");
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                return new ReferenceSessionInfo(false, null, err.ToString());

            return new ReferenceSessionInfo(true, FindUserId(root), null);
        }
    }

    private static string? FindUserId(JsonElement root)
    {
        foreach (var name in new[] { "id", "userId", "user_id" })
            if (root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.Number or JsonValueKind.String)
                return v.ToString();
        foreach (var nested in new[] { "data", "user", "userDetails" })
            if (root.TryGetProperty(nested, out var n) && n.ValueKind == JsonValueKind.Object)
                if (FindUserId(n) is { } inner) return inner;
        return null;
    }

    // ── Search ─────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ReferenceCompanyHint>> SearchCompaniesAsync(string query, int limit, CancellationToken ct)
    {
        EnsureConfigured();
        var payload = new { action = "getNameHints", q = query.Trim(), filters = "{}", offset = 0, limit };
        using var response = await SendSignedAsync("server/common/search/service.php", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's search returned HTTP {(int)response.StatusCode}.", retryable: (int)response.StatusCode >= 500);

        using var doc = ParseJsonOrThrow(body, "search");
        var hits = new List<ReferenceCompanyHint>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var cin = Text(item, "cin");
                var name = Text(item, "legal_name") ?? Text(item, "name");
                if (string.IsNullOrWhiteSpace(cin) || string.IsNullOrWhiteSpace(name)) continue;
                hits.Add(new ReferenceCompanyHint(name, cin.ToUpperInvariant(), Text(item, "bid"), Text(item, "status"), Text(item, "company_type")));
            }
        }
        return hits;
    }

    // ── Workbook exports ────────────────────────────────────────────────────────────────────────────

    /// <summary>Downloads the corporate or charge workbook to <paramref name="destinationPathWithoutExtension"/>
    /// and returns the full path written (extension chosen from the file signature: .xls for a legacy
    /// OLE workbook, .xlsx for an OOXML one). Throws <see cref="ReferenceToolException"/> when the tool
    /// answers with anything that is not a workbook (typically: company not unlocked / session expired).</summary>
    public async Task<string> DownloadWorkbookAsync(string cin, string bid, ReferenceWorkbookKind kind, string destinationPathWithoutExtension, CancellationToken ct)
    {
        EnsureConfigured();
        var printParams = JsonSerializer.Serialize(new
        {
            cin = cin.Trim().ToUpperInvariant(),
            bid,
            mime = "xls",
            unit = "Crore",
            context = kind == ReferenceWorkbookKind.Charge ? "CHARGE" : "ALL",
            platform = "b2c",
            token = $"{bid}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
        });
        var url = BuildUrl("server/common/publishing/service.php", new Dictionary<string, string>
        {
            ["action"] = "publishProbedData",
            ["bid"] = bid,
            ["v"] = _opts.ClientVersion,
            ["cv"] = _opts.AppVersion,
            ["printParams"] = printParams,
        });

        var tempPath = destinationPathWithoutExtension + ".download";
        var (header, contentType) = await StreamToFileAsync(url, tempPath, ct);
        var extension = header.AsSpan().StartsWith(OleSignature) ? ".xls"
            : header.AsSpan().StartsWith(ZipSignature) ? ".xlsx"
            : null;
        if (extension is null)
        {
            var snippet = await ReadSnippetAsync(tempPath);
            TryDelete(tempPath);
            var label = kind == ReferenceWorkbookKind.Charge ? "charge" : "corporate";
            throw new ReferenceToolException(
                $"The reference tool did not return a workbook for the {label} export (content-type '{contentType ?? "?"}'). " +
                $"This usually means the company is not unlocked in the tool, or the session cookie has expired. Response began: {snippet}");
        }

        var finalPath = destinationPathWithoutExtension + extension;
        TryDelete(finalPath);
        File.Move(tempPath, finalPath);
        return finalPath;
    }

    // ── Reference documents ─────────────────────────────────────────────────────────────────────────

    /// <summary>Fetches the registry of every filing PDF the tool holds for the company across its four
    /// sections. The first call returns each section's first page (100 rows observed) plus its total; when
    /// a total exceeds what was listed, further pages are requested with <c>offset</c>/<c>limit</c> on the
    /// same action until a page adds nothing new — that paging contract is inferred, not confirmed, so the
    /// caller must compare <see cref="ReferenceDocumentRegistry.ListedCount"/> with <see cref="ReferenceDocumentRegistry.TotalCount"/>
    /// and warn when they still differ.</summary>
    public async Task<ReferenceDocumentRegistry> GetReferenceDocumentsAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        var sections = RegistrySections.ToDictionary(s => s.Key, s => new SectionAccumulator(s.FolderName));

        var first = await FetchRegistryPageAsync(bid, offset: null, limit: null, ct);
        var pageSize = Math.Max(1, MergeRegistryPage(first, sections));

        const int maxPages = 200; // 20,000 rows — far above any real company; guards an endpoint that ignores offset
        for (var page = 1; page < maxPages; page++)
        {
            if (!sections.Values.Any(s => s.Docs.Count < s.Total)) break;
            var offset = page * pageSize;
            string next;
            try { next = await FetchRegistryPageAsync(bid, offset, pageSize, ct); }
            catch (ReferenceToolException ex)
            {
                logger.LogWarning(ex, "Reference-documents paging stopped at offset {Offset}", offset);
                break;
            }
            var added = MergeRegistryPage(next, sections);
            if (added == 0) break; // the endpoint ignored the offset, or we're past the end
        }

        return new ReferenceDocumentRegistry(RegistrySections
            .Select(s => new ReferenceDocumentSection(s.Key, s.FolderName, sections[s.Key].Total,
                sections[s.Key].Docs.Values.OrderByDescending(d => d.DocumentDate ?? DateTimeOffset.MinValue).ToList()))
            .ToList());
    }

    /// <summary>Parses one raw registry response on its own (no paging) — the shape the tool returns for
    /// the unpaged first call. Exposed for tests and for replaying a saved registry snapshot.</summary>
    public static ReferenceDocumentRegistry ParseRegistry(string body)
    {
        var sections = RegistrySections.ToDictionary(s => s.Key, s => new SectionAccumulator(s.FolderName));
        MergeRegistryPage(body, sections);
        return new ReferenceDocumentRegistry(RegistrySections
            .Select(s => new ReferenceDocumentSection(s.Key, s.FolderName, sections[s.Key].Total, sections[s.Key].Docs.Values.ToList()))
            .ToList());
    }

    private sealed class SectionAccumulator(string folderName)
    {
        public string FolderName { get; } = folderName;
        public int Total { get; set; }
        public Dictionary<string, ReferenceDocument> Docs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> FetchRegistryPageAsync(string bid, int? offset, int? limit, CancellationToken ct)
    {
        object payload = offset is null
            ? new { action = "referenceDocs", bid }
            : new { action = "referenceDocs", bid, offset = offset.Value, limit = limit!.Value };
        using var response = await SendSignedAsync("server/common/docService/service.php", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's document registry returned HTTP {(int)response.StatusCode}.", retryable: (int)response.StatusCode >= 500);
        return body;
    }

    /// <summary>Merges one registry response into the per-section accumulators; returns how many
    /// documents were new (0 ⇒ stop paging).</summary>
    private static int MergeRegistryPage(string body, Dictionary<string, SectionAccumulator> sections)
    {
        using var doc = ParseJsonOrThrow(body, "document registry");
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ReferenceToolException("The reference tool's document registry response was not a JSON object (session expired or company not unlocked?).");

        var added = 0;
        foreach (var (key, acc) in sections)
        {
            if (!doc.RootElement.TryGetProperty(key, out var raw)) continue;

            // Each section value is itself a JSON-encoded string — decode it, tolerating a direct object too.
            JsonElement section;
            JsonDocument? inner = null;
            if (raw.ValueKind == JsonValueKind.String)
            {
                try { inner = JsonDocument.Parse(raw.GetString() ?? "{}"); }
                catch (JsonException) { continue; }
                section = inner.RootElement;
            }
            else section = raw;

            using (inner)
            {
                if (section.ValueKind != JsonValueKind.Object) continue;
                if (section.TryGetProperty("totalCount", out var tc) && tc.TryGetInt32(out var total))
                    acc.Total = Math.Max(acc.Total, total);
                if (section.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in data.EnumerateArray())
                    {
                        var parsed = ParseDocument(item, key);
                        if (parsed is null || acc.Docs.ContainsKey(parsed.DocId)) continue;
                        acc.Docs[parsed.DocId] = parsed;
                        added++;
                    }
                }
                acc.Total = Math.Max(acc.Total, acc.Docs.Count);
            }
        }
        return added;
    }

    private static ReferenceDocument? ParseDocument(JsonElement item, string sectionKey)
    {
        var awsPath = Text(item, "awsPath");
        var docId = Text(item, "docId") ?? Path.GetFileNameWithoutExtension(awsPath ?? "");
        if (string.IsNullOrWhiteSpace(awsPath) || string.IsNullOrWhiteSpace(docId)) return null;

        var attachments = new List<ReferenceDocumentAttachment>();
        if (item.TryGetProperty("attachments", out var atts) && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var att in atts.EnumerateArray())
            {
                var attPath = Text(att, "awsPath");
                if (string.IsNullOrWhiteSpace(attPath)) continue;
                var attName = Text(att, "mcaName") ?? Text(att, "localName") ?? Path.GetFileName(attPath);
                attachments.Add(new ReferenceDocumentAttachment(attName, attPath));
            }
        }

        DateTimeOffset? date = DateTimeOffset.TryParse(Text(item, "documentDate"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;
        double? sizeKb = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var sz) ? sz : null;
        var name = Text(item, "mcaName") ?? Text(item, "description") ?? Text(item, "formName") ?? docId;
        return new ReferenceDocument(docId, name, Text(item, "formName"), date, sizeKb, awsPath, Text(item, "section") ?? sectionKey, attachments);
    }

    // ── PDF streaming ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Downloads one PDF (a main e-form or an attachment) straight to disk. Verifies the
    /// <c>%PDF</c> signature — the endpoint answers a bad key or an expired session with HTTP 200 + JSON.</summary>
    public async Task DownloadPdfAsync(string bid, string userId, string awsPath, string did, string destinationPath, CancellationToken ct)
    {
        EnsureConfigured();
        var url = BuildUrl("server/common/docService/service.php", new Dictionary<string, string>
        {
            ["bid"] = bid,
            ["userId"] = userId,
            ["action"] = "downloadPdf",
            ["key"] = awsPath,
            ["did"] = did,
            ["platform"] = "b2c",
            ["v"] = _opts.ClientVersion,
            ["cv"] = _opts.AppVersion,
        });

        var tempPath = destinationPath + ".download";
        var (header, contentType) = await StreamToFileAsync(url, tempPath, ct);
        if (!header.AsSpan().StartsWith(PdfSignature))
        {
            var snippet = await ReadSnippetAsync(tempPath);
            TryDelete(tempPath);
            throw new ReferenceToolException($"Not a PDF (content-type '{contentType ?? "?"}'): {snippet}");
        }
        TryDelete(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────────

    private void EnsureConfigured()
    {
        if (!_opts.IsConfigured)
            throw new ReferenceToolException("Auto-fetch is not configured: set ReferenceTool:BaseUrl and ReferenceTool:SessionCookie (user-secrets locally, environment variables elsewhere).");
    }

    private async Task<HttpResponseMessage> SendSignedAsync(string path, object payload, CancellationToken ct)
    {
        var token = await SignAsync(payload, ct);
        var url = BuildUrl(path, new Dictionary<string, string> { ["qp"] = token, ["v"] = _opts.ClientVersion, ["cv"] = _opts.AppVersion });
        using var request = NewRequest(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    private async Task<(byte[] Header, string? ContentType)> StreamToFileAsync(string url, string tempPath, CancellationToken ct)
    {
        using var request = NewRequest(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ReferenceToolException($"The reference tool refused the request (HTTP {(int)response.StatusCode}) — the session cookie has probably expired.");
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool returned HTTP {(int)response.StatusCode}.",
                retryable: response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500);

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var header = new byte[8];
        var headerRead = 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                if (headerRead < header.Length)
                {
                    var take = Math.Min(header.Length - headerRead, read);
                    Array.Copy(buffer, 0, header, headerRead, take);
                    headerRead += take;
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        return (header, response.Content.Headers.ContentType?.MediaType);
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Cookie", _opts.SessionCookie.Trim());
        request.Headers.TryAddWithoutValidation("User-Agent", _opts.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", BaseUri.ToString());
        request.Headers.TryAddWithoutValidation("Origin", BaseUri.GetLeftPart(UriPartial.Authority));
        return request;
    }

    private Uri BaseUri => new(_opts.BaseUrl.TrimEnd('/') + "/");

    private string BuildUrl(string path, IReadOnlyDictionary<string, string> query)
    {
        var sb = new StringBuilder(new Uri(BaseUri, path).ToString()).Append('?');
        var firstParam = true;
        foreach (var (k, v) in query)
        {
            if (!firstParam) sb.Append('&');
            firstParam = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }

    /// <summary>HS256-signs the action payload with the tool's published key.</summary>
    public async Task<string> SignAsync(object payload, CancellationToken ct)
    {
        var key = await GetSigningKeyAsync(ct);
        return SignJwt(payload, key);
    }

    public static string SignJwt(object payload, byte[] key)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson));
        var signingInput = $"{header}.{body}";
        var signature = Base64Url(HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(signingInput)));
        return $"{signingInput}.{signature}";
    }

    private async Task<byte[]> GetSigningKeyAsync(CancellationToken ct)
    {
        if (_signingKey is { } cached) return cached;
        await _keyGate.WaitAsync(ct);
        try
        {
            if (_signingKey is { } again) return again;
            var url = BuildUrl("server/common/jwt/service.php", new Dictionary<string, string>
            {
                ["action"] = "getJwtToken",
                ["v"] = _opts.ClientVersion,
                ["cv"] = _opts.AppVersion
            });
            using var request = NewRequest(url);
            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ReferenceToolException($"Could not obtain the reference tool's signing key (HTTP {(int)response.StatusCode}).", retryable: true);
            using var doc = ParseJsonOrThrow(body, "signing key");
            var hex = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("jwtToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(hex))
                throw new ReferenceToolException("The reference tool's signing-key response did not contain a key.");
            _signingKey = DecodeKey(hex);
            return _signingKey;
        }
        finally { _keyGate.Release(); }
    }

    /// <summary>The key is published hex-encoded; the front end decodes it to bytes before signing.
    /// A value that is not valid hex is used verbatim (UTF-8), which is what the tool's JS would do.</summary>
    public static byte[] DecodeKey(string published)
    {
        var trimmed = published.Trim();
        if (trimmed.Length % 2 == 0 && trimmed.Length > 0 && trimmed.All(Uri.IsHexDigit))
            return Convert.FromHexString(trimmed);
        return Encoding.UTF8.GetBytes(trimmed);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static JsonDocument ParseJsonOrThrow(string body, string what)
    {
        try { return JsonDocument.Parse(body); }
        catch (JsonException)
        {
            var snippet = body.Length > 160 ? body[..160] : body;
            throw new ReferenceToolException($"The reference tool's {what} response was not JSON (session expired?). It began: {snippet.ReplaceLineEndings(" ")}");
        }
    }

    private static string? Text(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(property, out var v)) return null;
        var text = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToString(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static async Task<string> ReadSnippetAsync(string path)
    {
        try
        {
            await using var fs = File.OpenRead(path);
            var buf = new byte[200];
            var n = await fs.ReadAsync(buf);
            return Encoding.UTF8.GetString(buf, 0, n).ReplaceLineEndings(" ");
        }
        catch { return "(unreadable)"; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}
