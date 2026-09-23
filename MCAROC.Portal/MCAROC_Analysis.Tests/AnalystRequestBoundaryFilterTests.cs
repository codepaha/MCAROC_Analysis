using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystRequestBoundaryFilterTests : IAsyncLifetime
{
    private IsolatedDatabase _database = null!;
    private long _analystId;
    private long _assignedRequestId;
    private long _unassignedRequestId;

    public async Task InitializeAsync()
    {
        _database = await TestDatabase.CreateIsolatedDatabaseAsync("AnalystBoundary");
        await using var db = CreateContext();
        var analyst = new Analyst
        {
            LoginName = $"BOUNDARY-{Guid.NewGuid():N}".ToUpperInvariant(), PasswordHash = "test",
            DisplayName = "Synthetic analyst", CreatedUtc = DateTime.UtcNow
        };
        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Analyst boundary test", CreatedDate = DateTime.UtcNow
        };
        var assigned = Request(client, "Assigned synthetic company");
        var unassigned = Request(client, "Unassigned synthetic company");
        db.AddRange(analyst, client, assigned, unassigned);
        await db.SaveChangesAsync();
        _analystId = analyst.AnalystId;
        _assignedRequestId = assigned.RequestId;
        _unassignedRequestId = unassigned.RequestId;
        db.AnalystAssignments.Add(new AnalystAssignment
        {
            AnalystId = _analystId, RequestId = _assignedRequestId, AssignedUtc = DateTime.UtcNow,
            AssignedByActorId = "test-operator"
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Fact]
    public async Task LegacyDetailRedirectsAssignedAnalystAndReturnsNotFoundForGuessedUnassignedId()
    {
        var assigned = await AuthorizeAsync("Requests", "Details", _assignedRequestId);
        var unassigned = await AuthorizeAsync("Requests", "Details", _unassignedRequestId);

        var redirect = Assert.IsType<RedirectResult>(assigned.Result);
        Assert.Equal($"/analyst/requests/{_assignedRequestId}", redirect.Url);
        Assert.IsType<NotFoundResult>(unassigned.Result);
    }

    [Fact]
    public async Task AssignedAnalystMayDownloadOwnedSourceButCannotRunOtherRequestActions()
    {
        var download = await AuthorizeAsync("Requests", "DownloadUploadedDocument", _assignedRequestId);
        var retry = await AuthorizeAsync("AutoFetch", "Retry", _assignedRequestId);
        var guessedRetry = await AuthorizeAsync("AutoFetch", "Retry", _unassignedRequestId);

        Assert.Null(download.Result);
        Assert.True(download.HttpContext.User.IsInRole(AnalystAccessConstants.Role));
        Assert.IsType<ForbidResult>(retry.Result);
        Assert.IsType<NotFoundResult>(guessedRetry.Result);
    }

    [Fact]
    public async Task AnalystCookieCannotOpenGlobalRequestListOrNonAnalystPortalRoutes()
    {
        var requestList = await AuthorizeAsync("Requests", "Index", null);
        var clients = await AuthorizeAsync("Clients", "Index", null);
        var analystPage = await AuthorizeAsync("AnalystDashboard", "Index", null);

        Assert.IsType<ForbidResult>(requestList.Result);
        Assert.IsType<ForbidResult>(clients.Result);
        Assert.Null(analystPage.Result);
    }

    private async Task<AuthorizationFilterContext> AuthorizeAsync(string controller, string action, long? requestId)
    {
        await using var db = CreateContext();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AnalystAccessConstants.AnalystIdClaimType, _analystId.ToString()),
             new Claim(ClaimTypes.Role, AnalystAccessConstants.Role)],
            AnalystAccessConstants.AuthenticationScheme));
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(new TestAuthenticationService(principal))
                .BuildServiceProvider()
        };
        var routeData = new RouteData();
        routeData.Values["controller"] = controller;
        routeData.Values["action"] = action;
        if (requestId is not null)
        {
            var key = controller == "Requests" && action == "DownloadUploadedDocument" ? "requestId" : "id";
            routeData.Values[key] = requestId.Value.ToString();
        }
        var descriptor = new ActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["controller"] = controller, ["action"] = action }
        };
        var actionContext = new ActionContext(http, routeData, descriptor, new ModelStateDictionary());
        var filterContext = new AuthorizationFilterContext(actionContext, []);
        await new AnalystRequestBoundaryFilter(new AnalystRequestAccessService(db)).OnAuthorizationAsync(filterContext);
        return filterContext;
    }

    private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(_database.ConnectionString).Options);

    private static McaRequest Request(Client client, string companyName) => new()
    {
        Client = client, EntityType = EntityType.Company, CompanyName = companyName,
        RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow
    };

    private sealed class TestAuthenticationService(ClaimsPrincipal principal) : IAuthenticationService
    {
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(scheme == AnalystAccessConstants.AuthenticationScheme
                ? AuthenticateResult.Success(new AuthenticationTicket(principal, scheme))
                : AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal user, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
