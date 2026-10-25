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

    /// <summary>The job-side session check: <see cref="CheckSessionAsync"/> plus health reporting, throwing a
    /// <see cref="ReferenceToolException"/> that keeps the failure's kind (an invalid session with no
    /// classified kind is <see cref="ReferenceToolFailureKind.SessionExpired"/>; a transport failure is
    /// <see cref="ReferenceToolFailureKind.Unavailable"/>) so the caller can tell a transient outage from a
    /// credentials problem. <see cref="ReferenceToolHealthProbe"/> deliberately calls
    /// <see cref="CheckSessionAsync"/> instead — it reports its own outcome, and reporting here as well
    /// would count one failure twice.</summary>
    public async Task<ReferenceSessionInfo> RequireValidSessionAsync(CancellationToken ct)
    {
        var callStartUtc = DateTime.UtcNow;
        ReferenceSessionInfo info;
        try
        {
            info = await CheckSessionAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            await ReportHealthSafeAsync(false, callStartUtc, ex.Message, authRejected: false, ct);
            throw new ReferenceToolException($"The reference tool could not be reached for the session check: {ex.Message}", ReferenceToolFailureKind.Unavailable, ex);
        }
        catch (ReferenceToolException ex) when (!ct.IsCancellationRequested)
        {
            await ReportHealthSafeAsync(false, callStartUtc, ex.Message, ex.Kind == ReferenceToolFailureKind.AuthRejected, ct);
            throw;
        }

        if (info.IsValid)
        {
            await ReportHealthSafeAsync(true, callStartUtc, null, false, ct);
            return info;
        }

        var kind = info.Kind ?? ReferenceToolFailureKind.SessionExpired;
        await ReportHealthSafeAsync(false, callStartUtc, info.Detail, kind == ReferenceToolFailureKind.AuthRejected, ct);
        var message = kind is ReferenceToolFailureKind.Unavailable or ReferenceToolFailureKind.RateLimited
            ? $"The reference tool is temporarily unavailable ({info.Detail})."
            : $"The reference-tool session is not valid ({info.Detail}). Sign in to the tool in a browser, copy the fresh Cookie header into ReferenceTool:SessionCookie and retry.";
        throw new ReferenceToolException(message, kind);
    }

    private async Task<ReferenceSessionInfo> TryGetUserDetailsAsync(CancellationToken ct)
    {
        using var response = await SendSignedAsync("server/user/userDetailsService.php", new { action = "getUserDetails" }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            return new ReferenceSessionInfo(false, null, $"HTTP {(int)response.StatusCode}", ClassifyHttpFailure(response.StatusCode));

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); }
        catch (JsonException) { return new ReferenceSessionInfo(false, null, "non-JSON response (logged out?)", ReferenceToolFailureKind.SessionExpired); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ReferenceSessionInfo(false, null, "unexpected response shape", ReferenceToolFailureKind.SessionExpired);
            if (root.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
                return new ReferenceSessionInfo(false, null, err.ToString(), ReferenceToolFailureKind.SessionExpired);

            var userId = FindUserId(root) ?? session.UserId;
            session.SetUserName(FindUserName(root));
            return new ReferenceSessionInfo(true, userId, null);
        }
    }

    /// <summary>The app version sent with every call: the one the tool last named as current, else the configured one.</summary>
    private string AppVersion => session.AppVersionOverride ?? _opts.AppVersion;

    /// <summary>The tool answers a call from an outdated app version with HTTP 426 and a body such as
    /// <c>{"version":"8.1.26","current_version":"8.1.28","app":"core"}</c>. Returns the current version when the reply is exactly that, else null.</summary>
    internal static string? CurrentVersionFrom(HttpStatusCode status, string? body)
    {
        if (status != HttpStatusCode.UpgradeRequired || string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("current_version", out var v) && v.ValueKind == JsonValueKind.String
                && v.GetString() is { } version && System.Text.RegularExpressions.Regex.IsMatch(version, @"^\d{1,4}(\.\d{1,4}){1,3}$"))
                return version;
        }
        catch (JsonException) { }
        return null;
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
                    $"Reference tool login rate limit reached ({_opts.MaxLoginsPerHour}/hour) — refusing to attempt another login.",
                    ReferenceToolFailureKind.RateLimited);

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

            async Task<(HttpResponseMessage Response, string Body)> SendLoginAsync()
            {
                var url = BuildUrl("server/user/login.php", new Dictionary<string, string>
                {
                    ["v"] = _opts.ClientVersion,
                    ["cv"] = AppVersion
                });
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Headers.TryAddWithoutValidation("User-Agent", _opts.UserAgent);
                request.Headers.TryAddWithoutValidation("Referer", BaseUri.ToString());
                request.Headers.TryAddWithoutValidation("Origin", BaseUri.GetLeftPart(UriPartial.Authority));
                request.Content = new FormUrlEncodedContent(
                [
                    new KeyValuePair<string, string>("pp", jwt)
                ]);
                var sent = await http.SendAsync(request, ct);
                return (sent, await sent.Content.ReadAsStringAsync(ct));
            }

            var (response, body) = await SendLoginAsync();
            // A tool release makes the configured app version outdated (HTTP 426 naming the current one). Adopt what the tool names and log in once
            // more, rather than stopping every fetch until the configuration is edited (end-to-end automation). Only ever once per login.
            if (CurrentVersionFrom(response.StatusCode, body) is { } current && session.AdoptAppVersion(current))
            {
                logger.LogWarning("The reference tool requires app version {Current} (configured {Configured}); using it from now on. Update ReferenceTool:AppVersion to match.",
                    current, _opts.AppVersion);
                response.Dispose();
                (response, body) = await SendLoginAsync();
            }
            using var _response = response;
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
                session.SetUserName(FindUserName(root));
                logger.LogInformation("Successfully logged into the reference tool as user {UserId}", userId);
                return new ReferenceSessionInfo(true, userId, null);
            }

            return new ReferenceSessionInfo(false, null, "No session cookies returned from login.", ReferenceToolFailureKind.ContractChanged);
            }
        }
        finally
        {
            session.LoginGate.Release();
        }
    }

    private static string? FindUserName(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "user_name", "userName" })
            if (Text(root, name) is { } v) return v;
        foreach (var nested in new[] { "data", "user", "userDetails" })
            if (root.TryGetProperty(nested, out var n) && n.ValueKind == JsonValueKind.Object && FindUserName(n) is { } inner)
                return inner;
        return null;
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
        return WithSessionRecoveryAsync(async () => (await SearchPageAsync(query, 0, limit, ct)).Hits, ct);
    }

    /// <summary>Every hit for <paramref name="query"/>, page by page, and whether the search was read to its end (#497). Pages are read until one comes
    /// back short. Rows without a CIN (partnerships, proprietorships) count toward a page being full, though they are not returned.
    /// <para>The tool returns its 25 best matches ranked by how well they match, and ignores the offset and the limit (2026-10-25, the #514 round:
    /// asked for rows 25-49 it sent rows 0-24 again). So a full page is also read to its end for this name when it is <b>ranked past the exact
    /// matches</b>: every row whose name is exactly the query comes first and at least one row that is not follows; an exact match cannot rank
    /// below a near name, so none is left unread (18 live searches, 15 with an exact match, all ranked so). A full page of exact matches only, or
    /// a page that mixes them, proves nothing, and the search is <b>not</b> complete; nor is one whose pages are all full or repeat.</para></summary>
    public Task<ReferenceSearchResult> SearchCompaniesExhaustivelyAsync(string query, int pageSize, int maxPages, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            var hits = new List<ReferenceCompanyHint>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < maxPages; page++)
            {
                var result = await SearchPageAsync(query, page * pageSize, pageSize, ct);
                if (page == 0 && result.RowKeys.Count >= pageSize && RankedPastExactMatches(query, result.RowNames))
                    return new ReferenceSearchResult(result.Hits.ToList(), Complete: true);
                var fresh = result.RowKeys.Count(k => seen.Add(k));
                if (result.RowKeys.Count > 0 && fresh == 0)
                    return new ReferenceSearchResult(hits, Complete: false);   // the same rows again: paging is not working
                hits.AddRange(result.Hits.Where(h => !hits.Any(x => x.Cin == h.Cin && x.NameHint == h.NameHint && x.Context == h.Context)));
                if (result.RowKeys.Count < pageSize)
                    return new ReferenceSearchResult(hits, Complete: true);
            }
            return new ReferenceSearchResult(hits, Complete: false);
        }, ct);
    }

    /// <summary>The page's rows in the tool's order show every exact match of <paramref name="query"/> before the first row that is not one, and at least
    /// one row that is not (see <see cref="SearchCompaniesExhaustivelyAsync"/>). Names compare as the company master does.</summary>
    internal static bool RankedPastExactMatches(string query, IReadOnlyList<string?> rowNames)
    {
        var key = CompanyMaster.CompanyNameNormalizer.Normalize(query).NameNormalized;
        if (key.Length == 0) return false;
        var firstNear = -1;
        for (var i = 0; i < rowNames.Count; i++)
        {
            var exact = CompanyMaster.CompanyNameNormalizer.Normalize(rowNames[i]).NameNormalized == key;
            if (!exact && firstNear < 0) firstNear = i;
            else if (exact && firstNear >= 0) return false;   // an exact match below a near name: the ranking cannot be relied on
        }
        return firstNear >= 0;
    }

    /// <summary>One page: the hits with a CIN, every row's key, and every row's name (the matched name, else the legal name) in the tool's order.</summary>
    private sealed record SearchPage(IReadOnlyList<ReferenceCompanyHint> Hits, IReadOnlyList<string> RowKeys, IReadOnlyList<string?> RowNames);

    private async Task<SearchPage> SearchPageAsync(string query, int offset, int limit, CancellationToken ct)
    {
        var payload = new { action = "getNameHints", q = query.Trim(), filters = "{}", offset, limit };
        using var response = await SendSignedAsync("server/common/search/service.php", payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's search returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));

        using var doc = ParseJsonOrThrow(body, "search");
        var hits = new List<ReferenceCompanyHint>();
        var rowKeys = new List<string>();
        var rowNames = new List<string?>();
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                // Every row counts toward the page, whether or not it is a company we can use.
                rowKeys.Add(string.Join('|', Text(item, "bid"), Text(item, "cin"), Text(item, "name_hint"), Text(item, "context")));
                rowNames.Add(Text(item, "name_hint") ?? Text(item, "legal_name") ?? Text(item, "name"));
                var cin = Text(item, "cin");
                var name = Text(item, "legal_name") ?? Text(item, "name");
                if (string.IsNullOrWhiteSpace(cin) || string.IsNullOrWhiteSpace(name)) continue;
                // Field names as the tool sends them (a recorded reply, 2026-10-24); the older status/company_type names are kept as fallbacks.
                var address = item.TryGetProperty("registered_address", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
                DateOnly? incorporated = DateOnly.TryParseExact(Text(item, "incorporation_date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                hits.Add(new ReferenceCompanyHint(name, cin.ToUpperInvariant(), Text(item, "bid"),
                    Text(item, "company_llp_status") ?? Text(item, "status"), Text(item, "entity_type") ?? Text(item, "company_type"),
                    Text(item, "name_hint"), Text(item, "context"),
                    address.ValueKind == JsonValueKind.Object ? Text(address, "state") : null,
                    address.ValueKind == JsonValueKind.Object ? Text(address, "pinCode") : null,
                    incorporated));
            }
        }
        return new SearchPage(hits, rowKeys, rowNames);
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
            ["cv"] = AppVersion,
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
    /// sections. The first call (action "referenceDocs") returns each section's initial page (up to 100
    /// rows) plus its totalCount; when a total exceeds what was listed, further pages are requested
    /// per-section using getRefDocs, getIncorporationDocs, or getScannedDocs with offset and count (#515)
    /// until all documents are retrieved or a page adds nothing new.</summary>
    public async Task<ReferenceDocumentRegistry> GetReferenceDocumentsAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        var sections = RegistrySections.ToDictionary(s => s.Key, s => new SectionAccumulator(s.FolderName));

        await FetchAndMergeInitialRegistryAsync(bid, sections, ct);

        const int pageSize = 100;
        const int maxPagesPerSection = 200; // 20,000 rows per section — far above any real company

        foreach (var (key, acc) in sections)
        {
            var page = 1;
            while (acc.Docs.Count < acc.Total && page < maxPagesPerSection)
            {
                var offset = acc.Docs.Count;
                int added;
                try
                {
                    added = await FetchAndMergeSectionPageAsync(bid, key, offset, pageSize, acc, ct);
                }
                catch (ReferenceToolException ex)
                {
                    logger.LogWarning(ex, "Reference-documents paging stopped for section {Section} at offset {Offset}", key, offset);
                    break;
                }

                if (added == 0) break; // endpoint ignored offset or no new items
                page++;
            }
            acc.Total = Math.Max(acc.Total, acc.Docs.Count);
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

    /// <summary>Fetches the unpaged registry snapshot covering the first page of all four sections.</summary>
    private Task<int> FetchAndMergeInitialRegistryAsync(string bid, Dictionary<string, SectionAccumulator> sections, CancellationToken ct) =>
        WithSessionRecoveryAsync(async () =>
        {
            using var response = await SendSignedAsync("server/common/docService/service.php", new { action = "referenceDocs", bid }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ReferenceToolException($"The reference tool's document registry returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));
            return MergeRegistryPage(body, sections);
        }, ct);

    /// <summary>Fetches subsequent pages per-section using the section's dedicated action (#515).</summary>
    private Task<int> FetchAndMergeSectionPageAsync(string bid, string sectionKey, int offset, int count, SectionAccumulator acc, CancellationToken ct) =>
        WithSessionRecoveryAsync(async () =>
        {
            object payload = sectionKey switch
            {
                "annual" => new { action = "getRefDocs", bid, section = "AnnualReturnsEform", offset, count, platform = "b2c" },
                "charge" => new { action = "getRefDocs", bid, section = "ChargeDocuments", offset, count, platform = "b2c" },
                "incorporation" => new { action = "getIncorporationDocs", bid, offset, count, platform = "b2c" },
                "scanned" => new { action = "getScannedDocs", bid, offset, count, platform = "b2c" },
                _ => throw new ArgumentException($"Unknown section key '{sectionKey}'", nameof(sectionKey))
            };

            using var response = await SendSignedAsync("server/common/docService/service.php", payload, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new ReferenceToolException($"The reference tool's document section returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));
            return MergeSectionPage(body, acc, sectionKey);
        }, ct);

    /// <summary>Merges one subsequent section page into the accumulator; returns how many documents were new.</summary>
    private static int MergeSectionPage(string body, SectionAccumulator acc, string sectionKey)
    {
        using var doc = ParseJsonOrThrow(body, "document registry section");
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            throw new ReferenceToolException("The reference tool's section page was not a JSON object.", ReferenceToolFailureKind.SessionExpired);

        if (doc.RootElement.TryGetProperty("totalCount", out var tc) && tc.TryGetInt32(out var total))
            acc.Total = Math.Max(acc.Total, total);

        var added = 0;
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var parsed = ParseDocument(item, sectionKey);
                if (parsed is null || acc.Docs.ContainsKey(parsed.DocId)) continue;
                acc.Docs[parsed.DocId] = parsed;
                added++;
            }
        }
        return added;
    }

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

    /// <summary>Downloads one filing document (a main e-form or an attachment: a PDF, or an XBRL .xml / .xlsx / .xls attachment)
    /// straight to disk and returns its extension, read from the content. The endpoint answers a bad key or an expired session
    /// with HTTP 200 + JSON (or a page), and only that body means logged out; any other unrecognised body fails without a re-login.</summary>
    public Task<string> DownloadDocumentAsync(string bid, string userId, string awsPath, string did, string destinationPath, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(() => DownloadDocumentCoreAsync(bid, userId, awsPath, did, destinationPath, ct), ct);
    }

    private async Task<string> DownloadDocumentCoreAsync(string bid, string userId, string awsPath, string did, string destinationPath, CancellationToken ct)
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
            ["cv"] = AppVersion,
        });

        var tempPath = destinationPath + ".download";
        var (header, contentType) = await StreamToFileAsync(url, tempPath, ct);
        var head = header.AsSpan().StartsWith(PdfSignature) ? header : DownloadedFileType.ReadHead(tempPath);
        var extension = DownloadedFileType.ExtensionOf(head);
        if (extension is null)
        {
            var snippet = await ReadSnippetAsync(tempPath);
            TryDelete(tempPath);
            if (DownloadedFileType.LooksLikeSessionReply(head))
                throw new ReferenceToolException($"Not a document (content-type '{contentType ?? "?"}'): {snippet}", ReferenceToolFailureKind.SessionExpired);
            throw new ReferenceToolException($"Unrecognised file type (content-type '{contentType ?? "?"}'): {snippet}", ReferenceToolFailureKind.Other);
        }
        TryDelete(destinationPath);
        File.Move(tempPath, destinationPath);
        return extension;
    }

    // ── Unlock / refresh lifecycle (docs/reference-tool-refresh-unlock-contract.md) ─────────────────
    // Every call here is free; the only paid call (addAsset) belongs to the approval-gated unlock (#266).

    /// <summary><c>getAssetTeams</c> — the authoritative locked/unlocked check. Unlocked when any of the account's teams holds the company (the earliest
    /// date is reported); the first team is the one an unlock would be made for.</summary>
    public Task<ReferenceAssetStatus> GetAssetStatusAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/user/service.php", new { action = "getAssetTeams", bid }, "asset status", ct);
            if (!doc.RootElement.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array || teams.GetArrayLength() == 0)
                throw new ReferenceToolException("The reference tool's asset-status response had no teams.", ReferenceToolFailureKind.ContractChanged);
            // The account can hold several teams. The company is unlocked for us if ANY of them holds it: reading only the first team took an
            // already-unlocked company for locked whenever the tool listed a team without it first, and asked for a paid unlock it did not need.
            // The first team stays the one an unlock would be made for.
            var first = teams[0];
            long? teamId = first.TryGetProperty("teamId", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var id) ? id : null;
            DateTimeOffset? addedAt = null;
            foreach (var team in teams.EnumerateArray())
                if (ParseTimestamp(Text(team, "addedAt")) is { } at && (addedAt is null || at < addedAt)) addedAt = at;
            return new ReferenceAssetStatus(teamId, addedAt);
        }, ct);
    }

    /// <summary>The raw MCA status for companies or LLPs, for display (the pre-flight check); decisions use <see cref="IsMcaAvailableForRefreshAsync"/>.</summary>
    public Task<ReferenceMcaStatus> GetMcaStatusAsync(bool isLlp, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/mcastatus/service.php",
                new { action = isLlp ? "getUpgradeStatusForLLPs" : "getUpgradeStatusForCompanies" }, "MCA status", ct);
            var status = Text(doc.RootElement, "mca_status")
                ?? throw new ReferenceToolException("The reference tool's MCA-status response had no mca_status.", ReferenceToolFailureKind.ContractChanged);
            return new ReferenceMcaStatus(status, Text(doc.RootElement, "alert_text"));
        }, ct);
    }

    /// <summary><c>getUpgradeStatusForCompanies</c>, or <c>getUpgradeStatusForLLPs</c> for an LLP (as the tool's own UI
    /// asks) — false while the government MCA portal is in maintenance, when the tool itself advises against starting a
    /// probe or refresh.</summary>
    public Task<bool> IsMcaAvailableForRefreshAsync(bool isLlp, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/mcastatus/service.php",
                new { action = isLlp ? "getUpgradeStatusForLLPs" : "getUpgradeStatusForCompanies" }, "MCA status", ct);
            return McaAllows(doc.RootElement, "refresh");
        }, ct);
    }

    /// <summary>NORMAL, or ALERT — a peak-filing-period notice the tool's own UI shows as "Do you want to continue?"
    /// and then proceeds past (it keeps retrying MCA itself). Any other status (maintenance) holds off.</summary>
    private bool McaAllows(JsonElement root, string purpose)
    {
        var status = Text(root, "mca_status")
            ?? throw new ReferenceToolException("The reference tool's MCA-status response had no mca_status.", ReferenceToolFailureKind.ContractChanged);
        if (string.Equals(status, "NORMAL", StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(status, "ALERT", StringComparison.OrdinalIgnoreCase)) return false;
        logger.LogWarning("The reference tool reports an MCA alert ({AlertText}); continuing with the {Purpose} as its own UI allows.",
            Text(root, "alert_text"), purpose);
        return true;
    }

    /// <summary>Asks the tool to probe the company: <c>requestProbeData</c> for one it has never probed (its UI's
    /// "Request a Probe"), else <c>requestProbeDataUpdate</c> (its "Refresh"); the update action returns HTTP 404 for a
    /// never-probed company. Free, and safe to repeat: the tool keeps one pending request per company.</summary>
    public Task RequestRefreshAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            var userName = session.UserName ?? (string.IsNullOrWhiteSpace(_opts.ToolUserName) ? null : _opts.ToolUserName.Trim())
                ?? throw new ReferenceToolException(
                    "The reference tool's user name is unknown (the session never reported one) — set ReferenceTool:ToolUserName.",
                    ReferenceToolFailureKind.Other);
            bool neverProbed;
            using (var dataStatus = await GetJsonAsync("server/common/api/service.php", new { action = "getDataStatus", bid }, "data status", ct))
                neverProbed = IsNeverProbed(dataStatus.RootElement);
            var action = neverProbed ? "requestProbeData" : "requestProbeDataUpdate";
            using var doc = await GetJsonAsync("server/common/urs/service.php",
                new { action, bid, userName, platform = "b2c" }, neverProbed ? "first probe request" : "refresh request", ct);
            var status = Text(doc.RootElement, "status");
            if (status is null || !(status.Equals("PENDING", StringComparison.OrdinalIgnoreCase) || status.Equals("REQUESTED", StringComparison.OrdinalIgnoreCase)))
                throw new ReferenceToolException($"The reference tool did not accept the refresh request (status '{status ?? "none"}').", ReferenceToolFailureKind.ContractChanged);
        }, ct);
    }

    /// <summary>The tool's own test for "Request a Probe": no QA date (<c>qa.date</c>), and nothing downloaded either.
    /// The QA block may sit at the root, under <c>data</c> or under <c>about_company.datastats</c>.</summary>
    public static bool IsNeverProbed(JsonElement root)
    {
        var data = root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object ? d
            : root.TryGetProperty("about_company", out var about) && about.ValueKind == JsonValueKind.Object
              && about.TryGetProperty("datastats", out var stats) && stats.ValueKind == JsonValueKind.Object ? stats
            : root;
        var qaDate = data.TryGetProperty("qa", out var qa) && qa.ValueKind == JsonValueKind.Object ? Text(qa, "date") : null;
        var downloaded = Text(data, "downloaded");
        var hasDownloadStatus = data.TryGetProperty("download_status", out var ds) && ds.ValueKind == JsonValueKind.Object;
        return string.IsNullOrWhiteSpace(qaDate) && string.IsNullOrWhiteSpace(downloaded) && !hasDownloadStatus;
    }

    /// <summary><c>getDataEntryRequestStatus</c> — whether a refresh is still pending at the tool.</summary>
    public Task<ReferenceRefreshStatus> GetRefreshStatusAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/urs/service.php",
                new { action = "getDataEntryRequestStatus", bid, platform = "b2c" }, "refresh status", ct);
            var status = Text(doc.RootElement, "status");
            if (string.Equals(status, "NO PENDING REQUEST", StringComparison.OrdinalIgnoreCase)) return ReferenceRefreshStatus.NoPendingRequest;
            if (string.Equals(status, "REQUESTED", StringComparison.OrdinalIgnoreCase) || string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase))
                return ReferenceRefreshStatus.Requested;
            throw new ReferenceToolException($"Unrecognised refresh status '{status ?? "none"}' from the reference tool.", ReferenceToolFailureKind.ContractChanged);
        }, ct);
    }

    /// <summary><c>getDataStatus</c> — how current the tool's data for the company is: <c>downloaded</c>, else
    /// <c>download_status.updatedAt</c>, else the date-only <c>qa.date</c> taken as the end of that India day.
    /// Null when none is present — callers treat that as "not known to be fresh".</summary>
    public Task<DateTimeOffset?> GetDataAsOfAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/api/service.php", new { action = "getDataStatus", bid }, "data status", ct);
            var root = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object ? data : doc.RootElement;
            if (ParseTimestamp(Text(root, "downloaded")) is { } downloaded) return (DateTimeOffset?)downloaded;
            if (root.TryGetProperty("download_status", out var ds) && ParseTimestamp(Text(ds, "updatedAt")) is { } updated) return updated;
            if (root.TryGetProperty("qa", out var qa) && Text(qa, "date") is { } qaDate
                && DateOnly.TryParseExact(qaDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return new DateTimeOffset(day.ToDateTime(new TimeOnly(23, 59, 59)), TimeSpan.FromHours(5.5));
            return null;
        }, ct);
    }

    /// <summary><c>getCompanyPreview</c> — the free identity check (CIN, legal name, status, state) that works
    /// on a locked company.</summary>
    public Task<ReferenceCompanyPreview> GetCompanyPreviewAsync(string bid, CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/api/service.php", new { action = "getCompanyPreview", bid }, "company preview", ct);
            if (!doc.RootElement.TryGetProperty("about_company", out var about) || !about.TryGetProperty("summary", out var summary))
                throw new ReferenceToolException("The reference tool's company preview had no summary.", ReferenceToolFailureKind.ContractChanged);
            var state = summary.TryGetProperty("registered_address", out var address) ? Text(address, "state") : null;
            return new ReferenceCompanyPreview(Text(summary, "cin")?.ToUpperInvariant(), Text(summary, "legal_name"), Text(summary, "company_llp_status"), state);
        }, ct);
    }

    /// <summary><c>getUpgradeStatusForUnlockingAsset</c> — false while the MCA portal is in maintenance, when
    /// the tool itself holds off unlocks.</summary>
    public Task<bool> IsMcaAvailableForUnlockAsync(CancellationToken ct)
    {
        EnsureConfigured();
        return WithSessionRecoveryAsync(async () =>
        {
            using var doc = await GetJsonAsync("server/common/mcastatus/service.php", new { action = "getUpgradeStatusForUnlockingAsset" }, "MCA status", ct);
            return McaAllows(doc.RootElement, "unlock");
        }, ct);
    }

    /// <summary><c>addAsset</c> — <b>spends one credit</b> to unlock the company. Deliberately <em>not</em> routed
    /// through the session-recovery wrapper: a paid call is never replayed automatically. The only caller is
    /// <see cref="CompanyUnlockService"/>, after an admission has been committed for it.</summary>
    public async Task AddAssetAsync(long teamId, string bid, string legalName, CancellationToken ct)
    {
        EnsureConfigured();
        var token = await SignAsync(new { action = "addAsset", teamId, bid, legalName }, ct);
        var url = BuildUrl("server/user/service.php", new Dictionary<string, string> { ["v"] = _opts.ClientVersion, ["cv"] = AppVersion });
        using var request = NewRequest(url);
        request.Method = HttpMethod.Post;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("pp", token)]);
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's unlock call returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));
        using var doc = ParseJsonOrThrow(body, "unlock");
        // An explicit error is a refusal. Anything else is not taken as proof either way: the tool's reply to addAsset has changed shape between
        // releases (a boolean statusCode, later a number), and a credit spent on an unlock that we then report as failed is the worst outcome.
        // The caller confirms the unlock by reading the company's asset status straight after this call; that, not this reply, decides.
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
            throw new ReferenceToolException($"The reference tool refused the unlock: {(body.Length > 200 ? body[..200] : body)}", ReferenceToolFailureKind.ContractChanged);
        if (!(doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("statusCode", out var ok) && ok.ValueKind == JsonValueKind.True))
            logger.LogWarning("The reference tool's unlock reply was not the expected confirmation ({Body}); the asset status read next decides.", body.Length > 200 ? body[..200] : body);
    }

    private async Task<JsonDocument> GetJsonAsync(string path, object payload, string what, CancellationToken ct)
    {
        using var response = await SendSignedAsync(path, payload, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ReferenceToolException($"The reference tool's {what} call returned HTTP {(int)response.StatusCode}.", ClassifyHttpFailure(response.StatusCode));
        var doc = ParseJsonOrThrow(body, what);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            throw new ReferenceToolException($"The reference tool's {what} response was not a JSON object.", ReferenceToolFailureKind.SessionExpired);
        }
        return doc;
    }

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

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
        var url = BuildUrl(path, new Dictionary<string, string> { ["qp"] = token, ["v"] = _opts.ClientVersion, ["cv"] = AppVersion });
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
                ["cv"] = AppVersion
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
