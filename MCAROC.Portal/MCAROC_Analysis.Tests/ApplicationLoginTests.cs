using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Services.ApplicationAuth;
using MCAROC_Analysis.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

// These middleware tests never query SQL or start workers: failures must be authentication failures,
// not database outages or effects from real paid integrations.
public sealed class ApplicationLoginTests : IDisposable
{
    private const string TestPassword = "Synthetic-Login-Only-42!";
    private readonly WebApplicationFactory<Program> factory;
    private readonly string hash = new PasswordHasher<string>().HashPassword("test@example.test", TestPassword);

    public ApplicationLoginTests()
    {
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ApplicationAuth:Username"] = "test@example.test",
                ["ApplicationAuth:PasswordHash"] = hash
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.RemoveAll<IAuditLogService>();
                services.AddSingleton<IAuditLogService, NoDatabaseAudit>();
            });
        });
    }

    private HttpClient Client() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false
    });

    private static async Task<string> Token(HttpClient client)
    {
        var html = await client.GetStringAsync("/login");
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return match.Groups[1].Value;
    }

    private static async Task<HttpResponseMessage> Login(HttpClient client, string password = TestPassword, string returnUrl = "/pre-login-reports")
    {
        var token = await Token(client);
        return await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "test@example.test", ["password"] = password,
            ["returnUrl"] = returnUrl, ["__RequestVerificationToken"] = token
        }));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Requests/New")]
    [InlineData("/Requests/AutoFetch")]
    [InlineData("/Requests/999999999/autofetch/status")]
    [InlineData("/pre-login-reports")]
    [InlineData("/pre-login-reports/mine")]
    [InlineData("/pre-login-reports/00000000-0000-0000-0000-000000000001/1/download")]
    [InlineData("/health")]
    [InlineData("/App_Data/PreLoginReports/private.docx")]
    public async Task AnonymousApplicationUrlsRequireLogin(string path)
    {
        var response = await Client().GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login?", response.Headers.Location!.ToString());
        Assert.DoesNotContain("/analyst/login", response.Headers.Location.ToString());
    }

    [Fact]
    public async Task RenderedLoginUsesSuppliedBranding()
    {
        var html = await Client().GetStringAsync("/login");
        Assert.Contains("/images/login-logo.png", html);
        Assert.Contains("/images/login-favicon.png", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
        Assert.DoesNotContain("usermcaroc@ct.com", html);
        // Keep the real rendered page beside test outputs for browser layout review.
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory, "application-login-preview.html"), html);
    }

    [Fact]
    public async Task ApiAndMutationClientsReceive401()
    {
        var client = Client();
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/pre-login-reports/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client().PostAsync("/pre-login-reports", new StringContent(""))).StatusCode);
    }

    [Fact]
    public async Task SuccessfulLoginAllowsReportsAndLogoutRevokesAccess()
    {
        var client = Client();
        var response = await Login(client);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/pre-login-reports", response.Headers.Location!.ToString());
        var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("mcaroc_application_auth="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/pre-login-reports")).StatusCode);
        var token = await Token(client);
        var logout = await client.PostAsync("/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/pre-login-reports")).StatusCode);
    }

    [Fact]
    public async Task WrongPasswordNeverIssuesSessionAndExternalReturnUrlIsRejected()
    {
        var failed = await Login(Client(), "incorrect");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Contains("Invalid login ID or password", await failed.Content.ReadAsStringAsync());
        Assert.False(failed.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(value => value.StartsWith("mcaroc_application_auth=")));
        var succeeded = await Login(Client(), returnUrl: "https://external.example/");
        Assert.Equal("/", succeeded.Headers.Location!.ToString());
    }

    [Fact]
    public async Task ApplicationAccountReachesInternalFeatures_ButNotTheAnalystArea()
    {
        var client = Client();
        await Login(client);
        // The single application user has full access: the former reviewer-only pages no longer bounce to a login.
        var reviewer = await client.GetAsync("/internal/calc-audit/1");
        Assert.NotEqual(HttpStatusCode.Redirect, reviewer.StatusCode);
        var analyst = await client.GetAsync("/analyst");
        Assert.Equal(HttpStatusCode.Redirect, analyst.StatusCode);
        Assert.Contains("/analyst/login", analyst.Headers.Location!.ToString());
    }

    [Theory]
    [InlineData("Analyst", "mcaroc_analyst_auth", true)]
    [InlineData("ApplicationUser", "mcaroc_application_auth", false)]
    public async Task RestrictedOrExpiredSessionsCannotAccessApplication(string scheme, string cookieName, bool analyst)
    {
        var client = Client();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(scheme);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, "Analyst"), new Claim(ClaimTypes.NameIdentifier, "1")], scheme);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            ExpiresUtc = analyst ? DateTimeOffset.UtcNow.AddHours(1) : DateTimeOffset.UtcNow.AddMinutes(-1)
        }, scheme);
        client.DefaultRequestHeaders.Add("Cookie", cookieName + "=" + options.TicketDataFormat.Protect(ticket));
        var response = await client.GetAsync("/Requests/New");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        if (analyst) Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        else Assert.Contains("/login?", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task PasswordRotationRejectsExistingSession()
    {
        var client = Client();
        await Login(client);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        configuration["ApplicationAuth:PasswordHash"] = new PasswordHasher<string>().HashPassword("test@example.test", "Rotated-Synthetic-42!");
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/pre-login-reports")).StatusCode);
    }

    [Fact]
    public async Task LoginRequiresAntiforgeryAndLimitsRepeatedAttempts()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Client().PostAsync("/login", new StringContent(""))).StatusCode);
        var client = Client();
        // The rejected antiforgery POST consumes one of the five requests in this window.
        for (var attempt = 0; attempt < 4; attempt++) Assert.Equal(HttpStatusCode.OK, (await Login(client, "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Login(client, "wrong")).StatusCode);
    }

    [Theory]
    [InlineData("/login")]
    [InlineData("/health/live")]
    [InlineData("/css/login.css")]
    [InlineData("/images/login-logo.png")]
    public async Task LoginAndBundledAssetsRemainAnonymous(string path) =>
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync(path)).StatusCode);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("malformed-hash")]
    public void MissingOrMalformedConfigurationFailsClosed(string? invalidHash)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationAuth:PasswordHash"] = invalidHash
        }).Build();
        Assert.False(new ApplicationCredentialChecker(configuration).Verify("usermcaroc@ct.com", TestPassword));
    }

    public void Dispose() => factory.Dispose();

    private sealed class NoDatabaseAudit : IAuditLogService
    {
        public Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class => Task.CompletedTask;
        public Task TryLogAsync(AuditEvent evt, CancellationToken ct = default) => Task.CompletedTask;
    }
}
