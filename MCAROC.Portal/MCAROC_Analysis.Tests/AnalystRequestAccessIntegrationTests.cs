using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>
/// Exercises the analyst boundary against SQL Server rather than an in-memory substitute. In particular,
/// reassignment must revoke the previous analyst immediately: neither a list query nor the authorization
/// handler may retain the old assignment as authority.
/// </summary>
public sealed class AnalystRequestAccessIntegrationTests : IAsyncLifetime
{
    private static AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(TestDatabase.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await TestDatabase.MigrateAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AccessibleRequests_ReturnsOnlyTheActiveAnalystsCurrentAssignments()
    {
        await using var db = CreateContext();
        var fixture = await SeedAsync(db);

        var access = new AnalystRequestAccessService(db);
        var visibleRequestIds = await access.AccessibleRequests(fixture.FirstAnalyst.AnalystId)
            .Select(request => request.RequestId)
            .ToListAsync();

        Assert.Equal([fixture.FirstAssignedRequest.RequestId], visibleRequestIds);
        Assert.DoesNotContain(fixture.SecondAssignedRequest.RequestId, visibleRequestIds);
        Assert.DoesNotContain(fixture.UnassignedRequest.RequestId, visibleRequestIds);
    }

    [Fact]
    public async Task CanAccessAsync_RejectsUnassignedAndInactiveAnalysts()
    {
        await using var db = CreateContext();
        var fixture = await SeedAsync(db);
        var access = new AnalystRequestAccessService(db);

        Assert.True(await access.CanAccessAsync(Principal(fixture.FirstAnalyst.AnalystId), fixture.FirstAssignedRequest.RequestId));
        Assert.False(await access.CanAccessAsync(Principal(fixture.SecondAnalyst.AnalystId), fixture.FirstAssignedRequest.RequestId));
        Assert.False(await access.CanAccessAsync(Principal(fixture.FirstAnalyst.AnalystId), fixture.UnassignedRequest.RequestId));
        Assert.False(await access.CanAccessAsync(Principal(fixture.InactiveAnalyst.AnalystId), fixture.SecondAssignedRequest.RequestId));
    }

    [Fact]
    public async Task AuthorizationHandler_RechecksTheDatabaseAfterReassignment()
    {
        await using var db = CreateContext();
        var fixture = await SeedAsync(db);
        var access = new AnalystRequestAccessService(db);
        var handler = new AnalystRequestAuthorizationHandler(access);

        var beforeReassignment = await AuthorizeAsync(handler, fixture.FirstAnalyst.AnalystId, fixture.FirstAssignedRequest);
        Assert.True(beforeReassignment.HasSucceeded);

        var assignment = await db.AnalystAssignments.SingleAsync(item => item.RequestId == fixture.FirstAssignedRequest.RequestId);
        assignment.AnalystId = fixture.SecondAnalyst.AnalystId;
        await db.SaveChangesAsync();

        var formerAnalyst = await AuthorizeAsync(handler, fixture.FirstAnalyst.AnalystId, fixture.FirstAssignedRequest);
        var newAnalyst = await AuthorizeAsync(handler, fixture.SecondAnalyst.AnalystId, fixture.FirstAssignedRequest);

        Assert.False(formerAnalyst.HasSucceeded);
        Assert.True(newAnalyst.HasSucceeded);
    }

    private static async Task<AuthorizationHandlerContext> AuthorizeAsync(
        IAuthorizationHandler handler, long analystId, McaRequest request)
    {
        var context = new AuthorizationHandlerContext(
            [new AnalystRequestAccessRequirement()], Principal(analystId), request);
        await handler.HandleAsync(context);
        return context;
    }

    private static ClaimsPrincipal Principal(long analystId) => new(new ClaimsIdentity(
        [new Claim(AnalystAccessConstants.AnalystIdClaimType, analystId.ToString())], "Analyst"));

    private static async Task<Fixture> SeedAsync(AppDbContext db)
    {
        var first = new Analyst { LoginName = $"ANALYST-{Guid.NewGuid():N}", PasswordHash = "test", DisplayName = "First", CreatedUtc = DateTime.UtcNow };
        var second = new Analyst { LoginName = $"ANALYST-{Guid.NewGuid():N}", PasswordHash = "test", DisplayName = "Second", CreatedUtc = DateTime.UtcNow };
        var inactive = new Analyst { LoginName = $"ANALYST-{Guid.NewGuid():N}", PasswordHash = "test", DisplayName = "Inactive", IsActive = false, CreatedUtc = DateTime.UtcNow, DisabledUtc = DateTime.UtcNow };
        var client = new Client { ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Analyst access test", CreatedDate = DateTime.UtcNow };
        var firstRequest = Request(client);
        var secondRequest = Request(client);
        var unassignedRequest = Request(client);

        db.AddRange(first, second, inactive, client, firstRequest, secondRequest, unassignedRequest);
        await db.SaveChangesAsync();

        db.AnalystAssignments.AddRange(
            new AnalystAssignment { AnalystId = first.AnalystId, RequestId = firstRequest.RequestId, AssignedUtc = DateTime.UtcNow, AssignedByActorId = "test" },
            new AnalystAssignment { AnalystId = inactive.AnalystId, RequestId = secondRequest.RequestId, AssignedUtc = DateTime.UtcNow, AssignedByActorId = "test" });
        await db.SaveChangesAsync();

        return new Fixture(first, second, inactive, firstRequest, secondRequest, unassignedRequest);
    }

    private static McaRequest Request(Client client) => new()
    {
        Client = client,
        EntityType = EntityType.Company,
        CompanyName = "Analyst access test",
        RequestNumber = $"TEST-{Guid.NewGuid():N}",
        CreatedDate = DateTime.UtcNow
    };

    private sealed record Fixture(
        Analyst FirstAnalyst,
        Analyst SecondAnalyst,
        Analyst InactiveAnalyst,
        McaRequest FirstAssignedRequest,
        McaRequest SecondAssignedRequest,
        McaRequest UnassignedRequest);
}
