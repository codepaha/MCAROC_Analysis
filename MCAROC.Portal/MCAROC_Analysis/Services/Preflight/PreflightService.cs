using System.Diagnostics;
using System.Net.Sockets;
using Google.Apis.Auth.OAuth2;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Preflight;

public enum PreflightOutcome { Pass, Warn, Fail }

/// <param name="Fix">What to do about a warning or failure; null when it passed.</param>
public sealed record PreflightCheck(string Key, string Name, PreflightOutcome Outcome, string Detail, string? Fix, long ElapsedMs);

public sealed record PreflightReport(DateTime CheckedUtc, IReadOnlyList<PreflightCheck> Checks)
{
    public PreflightOutcome Overall => Checks.Any(c => c.Outcome == PreflightOutcome.Fail) ? PreflightOutcome.Fail
        : Checks.Any(c => c.Outcome == PreflightOutcome.Warn) ? PreflightOutcome.Warn : PreflightOutcome.Pass;
}

/// <summary>The first minute of any E2E round (#514): everything a run depends on, checked before a request is created. On 2026-10-16 the
/// litigation API had been refusing connections for days, the portal password was unset and the dev DB was 7 migrations behind, all found
/// mid-run. Every check is free and read-only: nothing here spends a credit, starts a probe, registers a search, exports or calls a model.
/// The reference-tool session check may log in once if the session has expired, within the usual hourly login limit.</summary>
public sealed class PreflightService(IServiceScopeFactory scopes, IConfiguration config, IWebHostEnvironment env, ILogger<PreflightService> logger,
    TimeSpan? checkTimeout = null)
{
    public static readonly TimeSpan DefaultCheckTimeout = TimeSpan.FromSeconds(20);
    private readonly TimeSpan _checkTimeout = checkTimeout ?? DefaultCheckTimeout;
    private const long DiskWarnBytes = 20L * 1024 * 1024 * 1024;
    private const long DiskFailBytes = 5L * 1024 * 1024 * 1024;

    public async Task<PreflightReport> RunAsync(CancellationToken ct)
    {
        var checks = await Task.WhenAll(
            Run("reference-session", "Reference tool login", ReferenceSessionAsync, ct),
            Run("mca-company", "MCA status (companies)", (c) => McaStatusAsync(isLlp: false, c), ct),
            Run("mca-llp", "MCA status (LLPs)", (c) => McaStatusAsync(isLlp: true, c), ct),
            Run("litigation-api", "Litigation data API", LitigationApiAsync, ct),
            Run("order-host", "Order-document host", OrderHostAsync, ct),
            Run("portal-login", "Portal login", _ => Task.FromResult(PortalLogin()), ct),
            Run("database", "Database", DatabaseAsync, ct),
            Run("ai", "AI credentials", AiCredentialsAsync, ct),
            Run("court-agents", "Court update", _ => Task.FromResult(CourtAgents()), ct),
            Run("keywords", "Litigation keyword generation", _ => Task.FromResult(Keywords()), ct),
            Run("disk", "Disk space", _ => Task.FromResult(Disk()), ct));
        return new PreflightReport(DateTime.UtcNow, checks);
    }

    private sealed record Result(PreflightOutcome Outcome, string Detail, string? Fix = null);
    private static Result Pass(string detail) => new(PreflightOutcome.Pass, detail);
    private static Result Warn(string detail, string fix) => new(PreflightOutcome.Warn, detail, fix);
    private static Result Fail(string detail, string fix) => new(PreflightOutcome.Fail, detail, fix);

    // Each check gets its own timeout, and an exception is its failure, never the page's.
    private async Task<PreflightCheck> Run(string key, string name, Func<CancellationToken, Task<Result>> check, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_checkTimeout);
        Result result;
        try
        {
            result = await check(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = Fail($"No answer within {_checkTimeout.TotalSeconds:0} seconds.", "Check the service is up and reachable from this machine.");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Pre-flight check {Check} failed", key);
            result = Fail(ex.Message, "See the application log for details.");
        }
        return new PreflightCheck(key, name, result.Outcome, result.Detail, result.Fix, watch.ElapsedMilliseconds);
    }

    private async Task<Result> ReferenceSessionAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ReferenceToolClient>();
        if (!client.IsConfigured)
            return Fail("Not configured.", "Set ReferenceTool:BaseUrl and a session cookie or ReferenceTool:Username/Password in user-secrets.");
        var session = await client.CheckSessionAsync(ct);
        return session.IsValid
            ? Pass($"Signed in (user {session.UserId}).")
            : Fail($"Not signed in: {session.Detail}", "Check ReferenceTool:Username/Password, and that the hourly login limit has not been reached.");
    }

    private async Task<Result> McaStatusAsync(bool isLlp, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<ReferenceToolClient>();
        if (!client.IsConfigured) return Fail("Reference tool not configured.", "See the reference tool login check.");
        var status = await client.GetMcaStatusAsync(isLlp, ct);
        if (string.Equals(status.Status, "NORMAL", StringComparison.OrdinalIgnoreCase)) return Pass("NORMAL.");
        if (string.Equals(status.Status, "ALERT", StringComparison.OrdinalIgnoreCase))
            return Warn($"ALERT: {StripTags(status.AlertText) ?? "(no notice text)"}",
                "Runs proceed as the tool's own UI does, but MCA documents may arrive late. Profile 8 of the E2E matrix can run now.");
        return Fail($"{status.Status}: MCA is in maintenance.", "Refreshes and first probes wait until it is NORMAL or ALERT again; start the round later.");
    }

    private async Task<Result> LitigationApiAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var opts = scope.ServiceProvider.GetRequiredService<IOptions<BprLitigationOptions>>().Value;
        if (!opts.IsConfigured)
            return Warn("Not configured; litigation searches will be skipped.", "Set BprLitigation:BaseUrl, Id and SecretKey in user-secrets.");
        var uri = new Uri(opts.BaseUrl);
        using (var tcp = new TcpClient())
        {
            try
            {
                await tcp.ConnectAsync(uri.Host, uri.Port, ct);
            }
            catch (SocketException ex)
            {
                return Fail($"{uri.Host}:{uri.Port} {Describe(ex)}.", "The litigation data service is down or has moved; ask its team, or update BprLitigation:BaseUrl.");
            }
        }
        // Signing in is free and registers nothing; a search (bprjob/register) is never made here.
        await scope.ServiceProvider.GetRequiredService<BprLitigationClient>().AuthenticateAsync(ct);
        return Pass($"Reachable at {uri.Host}:{uri.Port}; sign-in accepted.");
    }

    private async Task<Result> OrderHostAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var hosts = scope.ServiceProvider.GetRequiredService<IOptions<BprLitigationOptions>>().Value.AllowedOrderDocumentHosts;
        if (hosts.Count == 0)
            return Warn("No order-document host is allowed; order PDFs will not download.", "Set BprLitigation:AllowedOrderDocumentHosts.");
        var unreachable = new List<string>();
        foreach (var host in hosts)
        {
            using var tcp = new TcpClient();
            try { await tcp.ConnectAsync(host, 443, ct); }
            catch (SocketException ex) { unreachable.Add($"{host} ({Describe(ex)})"); }
        }
        return unreachable.Count == 0
            ? Pass($"{string.Join(", ", hosts)} reachable on HTTPS.")
            : Fail($"Not reachable: {string.Join("; ", unreachable)}.", "Check the network or firewall for HTTPS to these hosts.");
    }

    private Result PortalLogin()
    {
        if (string.IsNullOrWhiteSpace(config["ApplicationAuth:PasswordHash"]))
            return Fail("ApplicationAuth:PasswordHash is not set, so nobody can sign in to the portal.", "Set it in user-secrets (a PasswordHasher hash of the password).");
        return Pass($"Sign-in configured for {config["ApplicationAuth:Username"] ?? "the default user"}.");
    }

    private async Task<Result> DatabaseAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
        // AUTO_CLOSE on makes every report fail with TaskCanceledException on this machine's SQL Express.
        var autoClose = await db.Database.SqlQueryRaw<bool>("SELECT is_auto_close_on AS [Value] FROM sys.databases WHERE name = DB_NAME()").SingleAsync(ct);
        var problems = new List<string>();
        if (pending.Count > 0) problems.Add($"{pending.Count} migration(s) not applied (latest {pending[^1]})");
        if (autoClose) problems.Add("AUTO_CLOSE is on");
        return problems.Count == 0
            ? Pass($"{db.Database.GetDbConnection().Database}: migrations current, AUTO_CLOSE off.")
            : Fail(string.Join("; ", problems) + ".", "Run `dotnet ef database update`, and `ALTER DATABASE [<name>] SET AUTO_CLOSE OFF`.");
    }

    private async Task<Result> AiCredentialsAsync(CancellationToken ct)
    {
        var projectId = config["GoogleCloud:ProjectId"];
        var path = config["GoogleCloud:CredentialsPath"];
        if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(path))
            return Fail("GoogleCloud:ProjectId or CredentialsPath is not set.", "Set both in user-secrets.");
        if (!File.Exists(path))
            return Fail($"The credentials file {path} does not exist.", "Point GoogleCloud:CredentialsPath at the service-account key file.");
        // A token only; no model is called, so this costs nothing.
        var credential = GoogleCredential.FromFile(path).CreateScoped("https://www.googleapis.com/auth/cloud-platform");
        await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
        return Pass($"Token issued for project {projectId}.");
    }

    private Result CourtAgents()
    {
        var off = new List<string>();
        if (!config.GetValue<bool>("LitigationPortals:Agents:Enabled")) off.Add("LitigationPortals:Agents:Enabled");
        if (!config.GetValue<bool>("LitigationModule:LivePublisher:Enabled")) off.Add("LitigationModule:LivePublisher:Enabled");
        return off.Count == 0
            ? Pass("Court agents and the live publisher are on.")
            : Warn($"Off: {string.Join(", ", off)}. Cases are not refreshed from the court portals.", "Set them to true in user-secrets for a post-login run.");
    }

    private Result Keywords() => config.GetValue<bool>("LitigationKeywords:UseGeneratedKeywords")
        ? Pass("Generated name forms are searched.")
        : Warn("LitigationKeywords:UseGeneratedKeywords is off: only the legal and former names are searched.", "Set it to true for a matrix run.");

    private Result Disk()
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(env.ContentRootPath))!);
        var free = drive.AvailableFreeSpace;
        var detail = $"{free / (1024.0 * 1024 * 1024):N1} GB free on {drive.Name}.";
        return free < DiskFailBytes ? Fail(detail, "Free up space: filings and OCR need several GB for a large company.")
            : free < DiskWarnBytes ? Warn(detail, "A large company's filings can need 10 GB or more.")
            : Pass(detail);
    }

    private static string Describe(SocketException ex) => ex.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "refused the connection (nothing is listening)",
        SocketError.TimedOut => "did not answer",
        SocketError.HostNotFound => "could not be resolved",
        _ => ex.Message
    };

    private static string? StripTags(string? html) =>
        html is null ? null : System.Text.RegularExpressions.Regex.Replace(html.Replace("<br>", " "), "<[^>]+>", "").Trim();
}
