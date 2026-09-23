using System.Security.Claims;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystDashboardQueryServiceTests : IAsyncLifetime
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
    public async Task BuildAsync_QueueAndCountsContainOnlyThePrincipalAnalystsAssignments()
    {
        await using var db = CreateContext();
        var suffix = Guid.NewGuid().ToString("N");
        var analyst = new Analyst { LoginName = $"DASH-{suffix}".ToUpperInvariant(), PasswordHash = "test", DisplayName = "Synthetic analyst", CreatedUtc = DateTime.UtcNow };
        var otherAnalyst = new Analyst { LoginName = $"OTHER-{suffix}".ToUpperInvariant(), PasswordHash = "test", DisplayName = "Other analyst", CreatedUtc = DateTime.UtcNow };
        var client = new Client { ClientCode = $"TST{suffix}"[..10], ClientName = "Analyst queue test", CreatedDate = DateTime.UtcNow };
        var assigned = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Assigned Demo Company",
            RequestNumber = $"TEST-{suffix}-A", RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow.AddDays(-3)
        };
        var anotherAnalystRequest = new McaRequest
        {
            Client = client, EntityType = EntityType.LLP, CompanyName = "Other Demo Company",
            RequestNumber = $"TEST-{suffix}-B", RequestStatus = RequestStatus.ExtractionFailed,
            CreatedDate = DateTime.UtcNow
        };
        var unassigned = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Unassigned Demo Company",
            RequestNumber = $"TEST-{suffix}-C", RequestStatus = RequestStatus.Created,
            CreatedDate = DateTime.UtcNow
        };

        db.AddRange(analyst, otherAnalyst, client, assigned, anotherAnalystRequest, unassigned);
        await db.SaveChangesAsync();
        db.AnalystAssignments.AddRange(
            new AnalystAssignment { AnalystId = analyst.AnalystId, RequestId = assigned.RequestId, AssignedUtc = DateTime.UtcNow, AssignedByActorId = "test" },
            new AnalystAssignment { AnalystId = otherAnalyst.AnalystId, RequestId = anotherAnalystRequest.RequestId, AssignedUtc = DateTime.UtcNow, AssignedByActorId = "test" });
        await db.SaveChangesAsync();

        var access = new AnalystRequestAccessService(db);
        var service = new AnalystDashboardQueryService(db, access);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(AnalystAccessConstants.AnalystIdClaimType, analyst.AnalystId.ToString()), new Claim(ClaimTypes.Name, "Synthetic analyst")],
            AnalystAccessConstants.AuthenticationScheme));

        var result = await service.BuildAsync(principal, "Assigned Demo", null, 1);

        Assert.NotNull(result);
        Assert.Equal("Synthetic analyst", result.AnalystName);
        Assert.Equal(1, result.AssignedCount);
        Assert.Equal(1, result.ReadyCount);
        Assert.Equal(0, result.FailedOrAttentionCount);
        Assert.Equal(1, result.TotalFilteredCount);
        Assert.Single(result.Requests);
        Assert.Equal(assigned.RequestId, result.Requests[0].RequestId);
    }

    [Fact]
    public async Task BuildAsync_RejectsAPrincipalWithoutAnAnalystId()
    {
        await using var db = CreateContext();
        var service = new AnalystDashboardQueryService(db, new AnalystRequestAccessService(db));

        var result = await service.BuildAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, null, 1);

        Assert.Null(result);
    }
}
