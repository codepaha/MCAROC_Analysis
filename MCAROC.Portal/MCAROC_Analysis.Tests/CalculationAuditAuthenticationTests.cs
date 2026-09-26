using System.Net;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.InternalAuth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace MCAROC_Analysis.Tests;

/// <summary>Real HTTP-pipeline coverage for the #164 access gate — proves the actual ASP.NET Core request
/// pipeline (routing → rate limiting → authentication → authorization → MVC) rejects an unauthenticated
/// caller, not just that [Authorize] is present as an attribute. A unit test that calls a controller action
/// directly (as most other tests in this project do) never exercises this middleware layer at all — only a
/// real hosted test server does.</summary>
public partial class CalculationAuditAuthenticationTests : IClassFixture<WebApplicationFactory<Program>>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;

    /// <summary>The app's pages query the shared test database, which only exists once some fixture has
    /// migrated it. Relying on another class to run first fails with HTTP 500 whenever xunit's collection
    /// order puts this class earlier (AutoFetchAuthenticationTests hit exactly that on #289) — so it
    /// migrates for itself, as every other DB-backed class does.</summary>
    public async Task InitializeAsync()
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(TestDatabase.ConnectionString).Options);
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public CalculationAuditAuthenticationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = TestDatabase.ConnectionString,
                    ["InternalAuth:ReviewerUsername"] = "testreviewer",
                    ["InternalAuth:ReviewerPasswordHash"] = InternalReviewerCredentialChecker.Hash("Correct-Horse-Battery-Staple-1"),
                    ["InternalAuth:ReviewerDisplayName"] = "Test Reviewer"
                });
            });
            // Pipeline-only coverage — see AutoFetchAuthenticationTests for why background workers must not
            // start against the shared test database.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
    }

    private HttpClient NoRedirectClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiForgeryTokenRegex();

    /// <summary>Fetches the login page on the given client (so its antiforgery cookie lands on that same
    /// client) and extracts the matching form token — real token handling, not a bypass, so a 400 from
    /// antiforgery would fail these tests loudly rather than silently passing for the wrong reason.</summary>
    private static async Task<string> FetchAntiForgeryTokenAsync(HttpClient client)
    {
        var page = await client.GetAsync("/internal/login");
        var html = await page.Content.ReadAsStringAsync();
        var match = AntiForgeryTokenRegex().Match(html);
        Assert.True(match.Success, "Antiforgery token not found in the rendered login page.");
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string username, string password)
    {
        var token = await FetchAntiForgeryTokenAsync(client);
        return await client.PostAsync("/internal/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["__RequestVerificationToken"] = token
        }));
    }

    [Fact]
    public async Task UnauthenticatedGet_ToCalcAuditIndex_IsChallengedToLogin_NeverReachesTheAction()
    {
        var response = await NoRedirectClient().GetAsync("/internal/calc-audit/1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/internal/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task UnauthenticatedPost_ToConfirm_IsChallenged_TheDiscrepancyIsNeverTouched()
    {
        // The exact scenario the finding raised: an anonymous caller trying to confirm a discrepancy
        // (which could create/release a delivery hold) must never reach the action at all. No antiforgery
        // token is fetched here on purpose — the authorization challenge must fire before the request ever
        // reaches the action-filter layer where antiforgery validation would otherwise run.
        var response = await NoRedirectClient().PostAsync(
            "/internal/calc-audit/discrepancy/1/confirm",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = "1", ["reviewerName"] = "anonymous-attacker", ["severity"] = "Minor"
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/internal/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task UnauthenticatedPost_ToReject_IsChallenged()
    {
        var response = await NoRedirectClient().PostAsync(
            "/internal/calc-audit/discrepancy/1/reject",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["requestId"] = "1", ["reviewerName"] = "anonymous-attacker", ["reason"] = "x"
            }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/internal/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task LoginPage_IsReachableAnonymously()
    {
        var response = await _factory.CreateClient().GetAsync("/internal/login");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Login_WithWrongPassword_NeverIssuesTheAuthCookie()
    {
        var client = NoRedirectClient();
        var response = await PostLoginAsync(client, "testreviewer", "wrong-password");

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Invalid username or password", body);
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(c => c.StartsWith("mcaroc_internal_auth", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Login_WithUnknownUsername_GetsTheSameGenericFailure_AsAWrongPassword()
    {
        // No username enumeration — both failure modes must be indistinguishable to the caller.
        var wrongPassword = await PostLoginAsync(NoRedirectClient(), "testreviewer", "wrong-password");
        var unknownUser = await PostLoginAsync(NoRedirectClient(), "nobody", "whatever");

        var wrongPasswordBody = await wrongPassword.Content.ReadAsStringAsync();
        var unknownUserBody = await unknownUser.Content.ReadAsStringAsync();
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Contains("Invalid username or password", wrongPasswordBody);
        Assert.Contains("Invalid username or password", unknownUserBody);
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_IssuesTheAuthCookie_HttpOnlyAndSameSiteStrict()
    {
        var client = NoRedirectClient();
        var response = await PostLoginAsync(client, "testreviewer", "Correct-Horse-Battery-Staple-1");

        Assert.True(response.Headers.TryGetValues("Set-Cookie", out var cookies));
        var authCookie = cookies!.FirstOrDefault(c => c.StartsWith("mcaroc_internal_auth", StringComparison.Ordinal));
        Assert.NotNull(authCookie);
        Assert.Contains("httponly", authCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", authCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Login_RateLimited_SixthRapidAttemptInTheWindowIsRejected()
    {
        // Fixed-window rate limiting is keyed on the remote IP (loopback for every TestServer call), so
        // reusing one client/token across attempts mirrors what six real rapid browser submissions from the
        // same machine would look like.
        var client = NoRedirectClient();
        var token = await FetchAntiForgeryTokenAsync(client);

        HttpResponseMessage last = null!;
        for (var i = 0; i < 6; i++)
        {
            last = await client.PostAsync("/internal/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "testreviewer", ["password"] = "wrong-password", ["__RequestVerificationToken"] = token
            }));
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
    }
}
