using System.Net;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
                    ["ApplicationAuth:Username"] = "test@example.test",
                    ["ApplicationAuth:PasswordHash"] = new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword("test@example.test", "Correct-Horse-Battery-Staple-1")
                });
            });
            // Pipeline-only coverage — see AutoFetchAuthenticationTests for why background workers must not
            // start against the shared test database.
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>());
        });
    }

    private HttpClient NoRedirectClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task UnauthenticatedGet_ToCalcAuditIndex_IsChallengedToLogin_NeverReachesTheAction()
    {
        var response = await NoRedirectClient().GetAsync("/internal/calc-audit/1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
        Assert.DoesNotContain("/internal/login", response.Headers.Location!.ToString());
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

        // Refused before the action runs: a 401 for a POST (or a redirect to the portal sign-in).
        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"Unexpected {response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.Redirect) Assert.DoesNotContain("/internal/login", response.Headers.Location!.ToString());
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

        Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"Unexpected {response.StatusCode}");
        if (response.StatusCode == HttpStatusCode.Redirect) Assert.DoesNotContain("/internal/login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task TheOldReviewerLogin_NoLongerExists()
    {
        // The controller and its cookie scheme are gone, so nothing can sign a caller in as a "reviewer".
        Assert.Null(typeof(Program).Assembly.GetType("MCAROC_Analysis.Controllers.InternalAuthController"));
        var schemes = _factory.Services.GetRequiredService<Microsoft.AspNetCore.Authentication.IAuthenticationSchemeProvider>();
        Assert.Null(await schemes.GetSchemeAsync("InternalReviewer"));

        // ...and the old URL is just an unknown page that sends anonymous callers to the portal sign-in.
        var response = await NoRedirectClient().GetAsync("/internal/login");
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }
}
