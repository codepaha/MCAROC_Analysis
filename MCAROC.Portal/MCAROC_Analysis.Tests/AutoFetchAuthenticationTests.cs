using System.Net;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Services.InternalAuth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace MCAROC_Analysis.Tests;

/// <summary>Real HTTP-pipeline coverage proving every AutoFetch endpoint is behind the "InternalReviewer"
/// cookie scheme — the finding this closes: AutoFetchController spends the app's own reference-tool
/// session credential and can trigger unbounded external downloads on the caller's behalf, so an anonymous
/// caller reaching it at all is the defect, not just a missing attribute. Mirrors
/// CalculationAuditAuthenticationTests' approach (deliberately self-contained rather than sharing its
/// private helpers, so this file's coverage doesn't depend on that unrelated file's internals): assert the
/// real request pipeline (routing → auth → MVC) rejects an unauthenticated caller, not just that
/// [Authorize] is present as an attribute.</summary>
public partial class AutoFetchAuthenticationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AutoFetchAuthenticationTests(WebApplicationFactory<Program> factory)
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
        });
    }

    // BaseAddress must be https:// — the InternalReviewer cookie is CookieSecurePolicy.Always, and the
    // client's own CookieContainer (HandleCookies defaults to true) silently drops a Secure cookie it
    // received unless subsequent requests are also seen as HTTPS. The in-memory TestServer doesn't need
    // real TLS for this to work; only the request's scheme matters for the cookie container's own check.
    private HttpClient NoRedirectClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost")
    });

    private static void AssertChallenged(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/internal/login", response.Headers.Location!.ToString());
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiForgeryTokenRegex();

    private static async Task LoginAsync(HttpClient client, string username, string password)
    {
        var page = await client.GetAsync("/internal/login");
        var html = await page.Content.ReadAsStringAsync();
        var token = AntiForgeryTokenRegex().Match(html).Groups[1].Value;
        var response = await client.PostAsync("/internal/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username, ["password"] = password, ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode); // successful login redirects away from /internal/login
    }

    [Fact]
    public async Task UnauthenticatedGet_ToNewForm_IsChallengedToLogin()
    {
        var response = await NoRedirectClient().GetAsync("/Requests/AutoFetch");
        AssertChallenged(response);
    }

    [Fact]
    public async Task UnauthenticatedPost_ToNewForm_IsChallenged_NoRequestOrJobIsCreated()
    {
        // No antiforgery token fetched on purpose — the auth challenge must fire before the request ever
        // reaches the action, i.e. before it could create an McaRequest or spend the reference-tool session.
        var response = await NoRedirectClient().PostAsync("/Requests/AutoFetch", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["ClientId"] = "1", ["Cin"] = "U45203OR1995PLC003982", ["EntityType"] = "Company"
        }));
        AssertChallenged(response);
    }

    [Fact]
    public async Task UnauthenticatedGet_ToSearch_IsChallenged_TheReferenceToolSessionIsNeverSpent()
    {
        var response = await NoRedirectClient().GetAsync("/Requests/AutoFetch/search?q=lodha");
        AssertChallenged(response);
    }

    [Fact]
    public async Task UnauthenticatedGet_ToStatus_IsChallenged()
    {
        var response = await NoRedirectClient().GetAsync("/Requests/1/autofetch/status");
        AssertChallenged(response);
    }

    [Fact]
    public async Task UnauthenticatedPost_ToRetry_IsChallenged()
    {
        var response = await NoRedirectClient().PostAsync("/Requests/1/autofetch/retry", new StringContent(""));
        AssertChallenged(response);
    }

    [Fact]
    public async Task AuthenticatedGet_ToNewForm_ReachesTheAction()
    {
        var client = NoRedirectClient();
        await LoginAsync(client, "testreviewer", "Correct-Horse-Battery-Staple-1");

        var response = await client.GetAsync("/Requests/AutoFetch");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedGet_ToStatus_ReachesTheAction_AndGets404ForAnUnknownRequest_NotAChallenge()
    {
        var client = NoRedirectClient();
        await LoginAsync(client, "testreviewer", "Correct-Horse-Battery-Staple-1");

        var response = await client.GetAsync("/Requests/999999999/autofetch/status");

        // 404 (not 302) proves the auth gate was passed and the action itself ran.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
