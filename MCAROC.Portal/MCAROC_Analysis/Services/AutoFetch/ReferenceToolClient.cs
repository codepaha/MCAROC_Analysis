using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
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
public sealed class ReferenceToolClient(HttpClient http, IOptions<ReferenceToolOptions> options, ReferenceToolSession session, IIntegrationHealthService health, ILogger<ReferenceToolClient> logger)
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

    public bool IsConfigured => _opts.IsConfigured;

    /// <summary>Returns the current session cookie — shared across every <see cref="ReferenceToolClient"/>
    /// instance via <see cref="ReferenceToolSession"/> (this client is transient; the session is not), so a
    /// login by one job scope is visible to every other. Falls back to (and seeds the session with) the
    /// operator-configured cookie the first time this is called with nothing discovered yet.</summary>
    public string GetActiveSessionCookie()
    {
        if (!string.IsNullOrWhiteSpace(session.Cookie))
            return session.Cookie;
        if (!string.IsNullOrWhiteSpace(_opts.SessionCookie))
        {
            session.SeedFromConfiguredCookie(_opts.SessionCookie.Trim());
            return session.Cookie ?? _opts.SessionCookie.Trim();
        }
        return string.Empty;
    }

    /// <summary>The tool identifies a company by <c>sha256(upper(CIN))</c> — deterministic, so no
    /// lookup is needed to go from a CIN/LLPIN to its business id.</summary>
    public static string ComputeBid(string cin) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(cin.Trim().ToUpperInvariant()))).ToLowerInvariant();

    // ── Session & Login ────────────────────────────────────────────────────────────────────────────

    public async Task<ReferenceSessionInfo> CheckSessionAsync(CancellationToken ct)
    {
        EnsureConfigured();

        if (string.IsNullOrWhiteSpace(GetActiveSessionCookie()) && _opts.CanAutoLogin)
        {
            var loginResult = await LoginAsync(ct);
            if (!loginResult.IsValid) return loginResult;
        }

        var session = await TryGetUserDetailsAsync(ct);
        if (!session.IsValid && _opts.CanAutoLogin)
        {
            logger.LogWarning("Reference tool session invalid ({Detail}). Attempting automated login...", session.Detail);
            var loginResult = await LoginAsync(ct);
            if (!loginResult.IsValid) return loginResult;
            session = await TryGetUserDetailsAsync(ct);
        }

        return session;
    }

    private async Task<ReferenceSessionInfo> TryGetUserDetailsAsync(CancellationToken ct)
    {
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

            var userId = FindUserId(root) ?? session.UserId;
            return new ReferenceSessionInfo(true, userId, null);
        }
    }

    public async Task<ReferenceSessionInfo> LoginAsync(CancellationToken ct)
    {
        if (!_opts.CanAutoLogin)
            return new ReferenceSessionInfo(false, null, "Reference tool username or password not configured.");

        var generationBeforeWaiting = session.Generation;
        await session.LoginGate.WaitAsync(ct);
        try
        {
            // Single-flight: if another caller already logged in while this one was waiting on the gate,
            // there is nothing to do — use the session it just set instead of logging in a second time.
            if (session.Generation != generationBeforeWaiting)
                return new ReferenceSessionInfo(true, session.UserId, null);

            if (!session.TryReserveLoginSlot(_opts.MaxLoginsPerHour, DateTime.UtcNow))
                return new ReferenceSessionInfo(false, null,
                    $"Reference tool login rate limit reached ({_opts.MaxLoginsPerHour}/hour) — refusing to attempt another login.");

            var key = await GetSigningKeyAsync(ct);
            var loginPayload = new
            {
                action = "login",
                u = _opts.Username.Trim(),
                p = _opts.Password.Trim(),
                mcc = 91,
                rememberMe = true
            };
            var jwt = SignJwt(loginPayload, key);

            var url = BuildUrl("server/user/login.php", new Dictionary<string, string>
            {
                ["v"] = _opts.ClientVersion,
                ["cv"] = _opts.AppVersion
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.TryAddWithoutValidation("User-Agent", _opts.UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", BaseUri.ToString());
            request.Headers.TryAddWithoutValidation("Origin", BaseUri.GetLeftPart(UriPartial.Authority));
            request.Content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("pp", jwt)
            ]);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                // A rejected login carried over HTTP status (401/403) is a credentials signal, not a
                // transient outage — classified AuthRejected so the circuit breaker opens immediately with
                // no threshold, same as an explicit "error" field below.
                var kind = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? ReferenceToolFailureKind.AuthRejected
                    : ReferenceToolFailureKind.Unavailable;
                return new ReferenceSessionInfo(false, null, $"Login failed with HTTP {(int)response.StatusCode}: {body}", kind);
            }

            JsonDocument doc;
            try { doc = ParseJsonOrThrow(body, "login"); }
            catch (ReferenceToolException ex) { return new ReferenceSessionInfo(false, null, ex.Message, ReferenceToolFailureKind.ContractChanged); }
            using (doc)
            {
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                return new ReferenceSessionInfo(false, null, err.ToString(), ReferenceToolFailureKind.AuthRejected);

            var userId = FindUserId(root);

            var cookieList = new List<string>();
            if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
            {
                foreach (var sc in setCookies)
                {
                    var cookiePart = sc.Split(';')[0].Trim();
                    if (!string.IsNullOrEmpty(cookiePart))
                        cookieList.Add(cookiePart);
                }
            }

            if (cookieList.Count > 0)
            {
                session.SetCookie(string.Join("; ", cookieList), userId);
                logger.LogInformation("Successfully logged into the reference tool as user {UserId}", userId);
                return new ReferenceSessionInfo(true, userId, null);
            }

            return new ReferenceSessionInfo(false, null, "No session cookies returned from login.");
            }
        }
        finally
        {
            session.LoginGate.Release();
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

    public Task<IReadOnlyList<ReferenceCompanyHint>> SearchCompaniesAsync(string query, int limit, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(() => SearchCompaniesCoreAsync(query, limit, ct), ct);
    }

    private async Task<IReadOnlyList<ReferenceCompanyHint>> SearchCompaniesCoreAsync(string query, int limit, CancellationToken ct)
    {
        var payload = new { action = "getNameHints", q = query.Trim(), filters = "{}", offset = 0, limit };
        using var response = await SendSignedAsync("server/common/search/service.php", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's search returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));

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
    public Task<string> DownloadWorkbookAsync(string cin, string bid, ReferenceWorkbookKind kind, string destinationPathWithoutExtension, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(() => DownloadWorkbookCoreAsync(cin, bid, kind, destinationPathWithoutExtension, ct), ct);
    }

    private async Task<string> DownloadWorkbookCoreAsync(string cin, string bid, ReferenceWorkbookKind kind, string destinationPathWithoutExtension, CancellationToken ct)
    {
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
            // Genuinely ambiguous from this call alone (§5.7.0 of the pipeline-automation plan): the tool
            // answers "not unlocked" and "session expired" identically here. Classified SessionExpired, the
            // recoverable case WithSessionRecoveryAsync already re-logs in for — not CompanyLocked, which is
            // reserved for the definitive getAssetTeams check #229/#266 wires in, not a guess made here.
            throw new ReferenceToolException(
                $"The reference tool did not return a workbook for the {label} export (content-type '{contentType ?? "?"}'). " +
                $"This usually means the company is not unlocked in the tool, or the session cookie has expired. Response began: {snippet}",
                ReferenceToolFailureKind.SessionExpired);
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

        var pageSize = Math.Max(1, await FetchAndMergeRegistryPageAsync(bid, offset: null, limit: null, sections, ct));

        const int maxPages = 200; // 20,000 rows — far above any real company; guards an endpoint that ignores offset
        for (var page = 1; page < maxPages; page++)
        {
            if (!sections.Values.Any(s => s.Docs.Count < s.Total)) break;
            var offset = page * pageSize;
            int added;
            try { added = await FetchAndMergeRegistryPageAsync(bid, offset, pageSize, sections, ct); }
            catch (ReferenceToolException ex)
            {
                logger.LogWarning(ex, "Reference-documents paging stopped at offset {Offset}", offset);
                break;
            }
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

    /// <summary>Fetches one registry page and merges it into <paramref name="sections"/> as a single
    /// recoverable unit (see <see cref="WithSessionRecoveryAsync{T}"/>) — the fetch and the "was this
    /// actually JSON, not a session-expired page" check must retry together, since the latter is what
    /// classifies <see cref="ReferenceToolFailureKind.SessionExpired"/> here, not the HTTP call itself.
    /// Safe to retry as a unit: <see cref="MergeRegistryPage"/> only mutates <paramref name="sections"/>
    /// after it has already validated the body is well-formed JSON.</summary>
    private Task<int> FetchAndMergeRegistryPageAsync(string bid, int? offset, int? limit, Dictionary<string, SectionAccumulator> sections, CancellationToken ct) =>
        WithSessionRecoveryAsync(async () =>
        {
            object payload = offset is null
                ? new { action = "referenceDocs", bid }
                : new { action = "referenceDocs", bid, offset = offset.Value, limit = limit!.Value };
            using var response = await SendSignedAsync("server/common/docService/service.php", payload, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ReferenceToolException($"The reference tool's document registry returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));
            return MergeRegistryPage(body, sections);
        }, ct);

    /// <summary>Merges one registry response into the per-section accumulators; returns how many
    /// documents were new (0 ⇒ stop paging).</summary>
    private static int MergeRegistryPage(string body, Dictionary<string, SectionAccumulator> sections)
    {
        using var doc = ParseJsonOrThrow(body, "document registry");
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ReferenceToolException("The reference tool's document registry response was not a JSON object (session expired or company not unlocked?).", ReferenceToolFailureKind.SessionExpired);

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
    public Task DownloadPdfAsync(string bid, string userId, string awsPath, string did, string destinationPath, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(() => DownloadPdfCoreAsync(bid, userId, awsPath, did, destinationPath, ct), ct);
    }

    private async Task DownloadPdfCoreAsync(string bid, string userId, string awsPath, string did, string destinationPath, CancellationToken ct)
    {
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
            throw new ReferenceToolException($"Not a PDF (content-type '{contentType ?? "?"}'): {snippet}", ReferenceToolFailureKind.SessionExpired);
        }
        TryDelete(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    // ── Plumbing ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Runs <paramref name="action"/>; on a <see cref="ReferenceToolFailureKind.SessionExpired"/>
    /// failure, re-logs in once (via the same single-flight, rate-limited <see cref="LoginAsync"/> every
    /// other caller shares — see <see cref="ReferenceToolSession"/>) and replays <paramref name="action"/>
    /// exactly once more. A second <see cref="ReferenceToolFailureKind.SessionExpired"/> is not retried
    /// again — it propagates, since by then either re-login itself failed (surfaced as the login's own
    /// failure kind) or the freshly-logged-in session was rejected again, which is a contract problem this
    /// method cannot fix by looping. When auto-login is not configured, the original exception propagates
    /// unchanged — there is nothing to recover with.
    ///
    /// Also the single place that reports every real call's outcome to <see
    /// cref="IIntegrationHealthService"/> (docs/pipeline-automation-plan.md §5.4) — every public entry
    /// point routes through here (directly or via <see cref="FetchAndMergeRegistryPageAsync"/>), so nothing
    /// needs to remember to report health separately.</summary>
    private async Task<T> WithSessionRecoveryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        var callStartUtc = DateTime.UtcNow;
        try
        {
            var result = await action();
            await ReportHealthSafeAsync(success: true, callStartUtc, error: null, authRejected: false, ct);
            return result;
        }
        catch (ReferenceToolException ex) when (ex.Kind == ReferenceToolFailureKind.SessionExpired && _opts.CanAutoLogin)
        {
            logger.LogWarning(ex, "Reference tool session expired; attempting one re-login and replay.");
            var login = await LoginAsync(ct);
            if (!login.IsValid)
            {
                await ReportHealthSafeAsync(false, callStartUtc, login.Detail, login.Kind == ReferenceToolFailureKind.AuthRejected, ct);
                throw new ReferenceToolException($"Session expired and automated re-login failed: {login.Detail}", login.Kind ?? ReferenceToolFailureKind.SessionExpired, ex);
            }
            try
            {
                var result = await action();
                await ReportHealthSafeAsync(true, callStartUtc, null, false, ct);
                return result;
            }
            catch (Exception inner) when (inner is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await ReportHealthSafeAsync(false, callStartUtc, inner.Message, inner is ReferenceToolException { Kind: ReferenceToolFailureKind.AuthRejected }, ct);
                throw;
            }
        }
        catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await ReportHealthSafeAsync(false, callStartUtc, ex.Message, ex is ReferenceToolException { Kind: ReferenceToolFailureKind.AuthRejected }, ct);
            throw;
        }
    }

    private Task WithSessionRecoveryAsync(Func<Task> action, CancellationToken ct) =>
        WithSessionRecoveryAsync<bool>(async () => { await action(); return true; }, ct);

    /// <summary>Health reporting must never be what breaks a caller — if the health row's own database
    /// write fails, log it and let the real result/exception from <paramref name="action"/> propagate
    /// unchanged rather than replacing it with a health-tracking error.</summary>
    private async Task ReportHealthSafeAsync(bool success, DateTime callStartUtc, string? error, bool authRejected, CancellationToken ct)
    {
        try
        {
            if (success)
                await health.ReportSuccessAsync(IntegrationName.ReferenceTool, callStartUtc, ct);
            else
                await health.ReportFailureAsync(IntegrationName.ReferenceTool, callStartUtc, error, authRejected,
                    _opts.BreakerThreshold, TimeSpan.FromMinutes(_opts.BreakerProbeLeaseMinutes), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to record reference-tool integration health");
        }
    }

    /// <summary>Classifies an ordinary (non-login) HTTP failure into a <see cref="ReferenceToolFailureKind"/>.
    /// 401/403 here means <see cref="ReferenceToolFailureKind.SessionExpired"/>, not <see
    /// cref="ReferenceToolFailureKind.AuthRejected"/> — that kind is reserved for the login call itself
    /// rejecting the *credentials*; a data call rejecting an already-established session is exactly the
    /// recoverable case <see cref="WithSessionRecoveryAsync{T}"/> re-logs in for.</summary>
    private static ReferenceToolFailureKind ClassifyHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ReferenceToolFailureKind.SessionExpired,
        HttpStatusCode.TooManyRequests => ReferenceToolFailureKind.RateLimited,
        HttpStatusCode.RequestTimeout => ReferenceToolFailureKind.Unavailable,
        _ when (int)status >= 500 => ReferenceToolFailureKind.Unavailable,
        _ => ReferenceToolFailureKind.Other
    };

    private void EnsureConfigured()
    {
        if (!_opts.IsConfigured)
            throw new ReferenceToolException("Auto-fetch is not configured: set ReferenceTool:BaseUrl and either ReferenceTool:SessionCookie or ReferenceTool:Username/Password.", ReferenceToolFailureKind.Other);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(string path, object payload, CancellationToken ct)
    {
        var token = await SignAsync(payload, ct);
        var url = BuildUrl(path, new Dictionary<string, string> { ["qp"] = token, ["v"] = _opts.ClientVersion, ["cv"] = _opts.AppVersion });
        using var request = NewRequest(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
    }

    /// <summary>Streams one response to disk with a hard, enforced-while-streaming size cap
    /// (<see cref="ReferenceToolOptions.MaxResponseBytes"/>) — never trusts a declared Content-Length
    /// alone, since a misbehaving or malicious response can omit it or lie about it. A response that
    /// exceeds the cap is aborted mid-stream and its partial file deleted before this method returns, so
    /// no caller ever sees a truncated-but-kept file on disk.</summary>
    private async Task<(byte[] Header, string? ContentType)> StreamToFileAsync(string url, string tempPath, CancellationToken ct)
    {
        using var request = NewRequest(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new ReferenceToolException($"The reference tool refused the request (HTTP {(int)response.StatusCode}) — the session cookie has probably expired.", ReferenceToolFailureKind.SessionExpired);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));

        var maxBytes = _opts.MaxResponseBytes;
        if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
            throw new ReferenceToolException($"The reference tool declared a {declaredLength:N0}-byte response, which exceeds the {maxBytes:N0}-byte limit — refused before downloading.", ReferenceToolFailureKind.Other);

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
        var header = new byte[8];
        var headerRead = 0;
        long totalWritten = 0;
        var exceeded = false;
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

                totalWritten += read;
                if (totalWritten > maxBytes)
                {
                    exceeded = true;
                    break; // stop reading immediately — do not keep pulling an oversized body off the wire
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }

        if (exceeded)
        {
            TryDelete(tempPath);
            throw new ReferenceToolException($"The reference tool's response exceeded the {maxBytes:N0}-byte limit while streaming; aborted and discarded.");
        }

        return (header, response.Content.Headers.ContentType?.MediaType);
    }

    private HttpRequestMessage NewRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var cookie = GetActiveSessionCookie();
        if (!string.IsNullOrWhiteSpace(cookie))
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
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
        if (session.SigningKey is { } cached) return cached;
        await session.SigningKeyGate.WaitAsync(ct);
        try
        {
            if (session.SigningKey is { } again) return again;
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
                throw new ReferenceToolException($"Could not obtain the reference tool's signing key (HTTP {(int)response.StatusCode}).", ReferenceToolFailureKind.Unavailable);
            using var doc = ParseJsonOrThrow(body, "signing key");
            var hex = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("jwtToken", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(hex))
                throw new ReferenceToolException("The reference tool's signing-key response did not contain a key.", ReferenceToolFailureKind.ContractChanged);
            var decoded = DecodeKey(hex);
            session.SetSigningKey(decoded);
            return decoded;
        }
        finally { session.SigningKeyGate.Release(); }
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
            throw new ReferenceToolException($"The reference tool's {what} response was not JSON (session expired?). It began: {snippet.ReplaceLineEndings(" ")}", ReferenceToolFailureKind.SessionExpired);
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
