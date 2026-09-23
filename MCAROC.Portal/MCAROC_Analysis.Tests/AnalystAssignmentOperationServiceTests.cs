using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystAssignmentOperationServiceTests
{
    private static AppDbContext CreateContext(string connectionString) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlServer(connectionString).Options);

    [Fact]
    public async Task AssignAsync_ReassignsToTheOnlyActiveAnalyst_AndAuditsTheBeforeAndAfterState()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("AnalystAssignment");
        await using var db = CreateContext(isolated.ConnectionString);

        var activeAnalyst = new Analyst
        {
            LoginName = $"ACTIVE-{Guid.NewGuid():N}".ToUpperInvariant(),
            PasswordHash = "test", DisplayName = "Synthetic active", CreatedUtc = DateTime.UtcNow
        };
        var formerAnalyst = new Analyst
        {
            LoginName = $"FORMER-{Guid.NewGuid():N}".ToUpperInvariant(),
            PasswordHash = "test", DisplayName = "Synthetic former", IsActive = false,
            CreatedUtc = DateTime.UtcNow, DisabledUtc = DateTime.UtcNow
        };
        var client = new Client
        {
            ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Assignment operation test", CreatedDate = DateTime.UtcNow
        };
        var request = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Synthetic company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow
        };
        db.AddRange(activeAnalyst, formerAnalyst, client, request);
        await db.SaveChangesAsync();

        var existing = new AnalystAssignment
        {
            AnalystId = formerAnalyst.AnalystId, RequestId = request.RequestId,
            AssignedUtc = DateTime.UtcNow.AddDays(-5), AssignedByActorId = "old-operator", Reason = "prior"
        };
        db.AnalystAssignments.Add(existing);
        await db.SaveChangesAsync();

        var service = new AnalystAssignmentOperationService(db);
        var result = await service.AssignAsync(request.RequestId, "operator-42", "Follow-up required");

        Assert.True(result.Changed);
        Assert.Equal(existing.AnalystAssignmentId, result.AnalystAssignmentId);
        Assert.Equal(formerAnalyst.AnalystId, result.PreviousAnalystId);
        Assert.Equal(activeAnalyst.AnalystId, result.NewAnalystId);

        var updated = await db.AnalystAssignments.SingleAsync(item => item.RequestId == request.RequestId);
        Assert.Equal(activeAnalyst.AnalystId, updated.AnalystId);
        Assert.Equal("operator-42", updated.AssignedByActorId);
        Assert.Equal("Follow-up required", updated.Reason);

        var audit = await db.AuditLogs.SingleAsync(item => item.RequestId == request.RequestId);
        Assert.Equal(AuditActionType.AnalystAssignmentChanged, audit.Action);
        Assert.Equal("operator-42", audit.ActorId);
        Assert.Equal(existing.AnalystAssignmentId, audit.EntityId);
        Assert.Contains($"\"previousAnalystId\":{formerAnalyst.AnalystId}", audit.EventPayloadJson);
        Assert.Contains($"\"newAnalystId\":{activeAnalyst.AnalystId}", audit.EventPayloadJson);
        Assert.Contains("Follow-up required", audit.EventPayloadJson);

        var repeated = await service.AssignAsync(request.RequestId, "operator-43", "Follow-up required");
        Assert.False(repeated.Changed);
        Assert.Single(await db.AuditLogs.Where(item => item.RequestId == request.RequestId).ToListAsync());

        var newRequest = new McaRequest
        {
            Client = client, EntityType = EntityType.Company, CompanyName = "Second synthetic company",
            RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(newRequest);
        await db.SaveChangesAsync();
        var created = await service.AssignAsync(newRequest.RequestId, "operator-42", "Initial allocation");
        var createdAudit = await db.AuditLogs.SingleAsync(item => item.RequestId == newRequest.RequestId);
        Assert.Equal(created.AnalystAssignmentId, createdAudit.EntityId);
        Assert.Contains($"\"analystAssignmentId\":{created.AnalystAssignmentId}", createdAudit.EventPayloadJson);
    }

    [Fact]
    public async Task AssignAsync_AuditInsertFailure_RollsBackTheAssignment()
    {
        await using var isolated = await TestDatabase.CreateIsolatedDatabaseAsync("AnalystAssignmentAuditFailure");
        long requestId;
        long formerAnalystId;
        await using (var seed = CreateContext(isolated.ConnectionString))
        {
            var analyst = new Analyst
            {
                LoginName = $"ACTIVE-{Guid.NewGuid():N}".ToUpperInvariant(), PasswordHash = "test",
                DisplayName = "Synthetic active", CreatedUtc = DateTime.UtcNow
            };
            var former = new Analyst
            {
                LoginName = $"FORMER-{Guid.NewGuid():N}".ToUpperInvariant(), PasswordHash = "test",
                DisplayName = "Synthetic former", IsActive = false, CreatedUtc = DateTime.UtcNow,
                DisabledUtc = DateTime.UtcNow
            };
            var client = new Client
            {
                ClientCode = $"TST{Guid.NewGuid():N}"[..10], ClientName = "Assignment audit failure test",
                CreatedDate = DateTime.UtcNow
            };
            var request = new McaRequest
            {
                Client = client, EntityType = EntityType.Company, CompanyName = "Synthetic company",
                RequestNumber = $"TEST-{Guid.NewGuid():N}", CreatedDate = DateTime.UtcNow
            };
            seed.AddRange(analyst, former, client, request);
            await seed.SaveChangesAsync();
            requestId = request.RequestId;
            formerAnalystId = former.AnalystId;
            seed.AnalystAssignments.Add(new AnalystAssignment
            {
                AnalystId = formerAnalystId, RequestId = requestId, AssignedUtc = DateTime.UtcNow.AddDays(-1),
                AssignedByActorId = "seed-operator", Reason = "seed reason"
            });
            await seed.SaveChangesAsync();

            await seed.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER [dbo].[TR_TestRejectAuditInsert] ON [dbo].[AuditLogs]
                AFTER INSERT AS
                BEGIN
                    THROW 51001, 'Synthetic audit persistence failure.', 1;
                END
                """);
        }

        await using (var attempt = CreateContext(isolated.ConnectionString))
        {
            var service = new AnalystAssignmentOperationService(attempt);
            await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.AssignAsync(requestId, "operator-42", "Change must roll back"));
        }

        await using (var verify = CreateContext(isolated.ConnectionString))
        {
            var assignment = await verify.AnalystAssignments.SingleAsync(item => item.RequestId == requestId);
            Assert.Equal(formerAnalystId, assignment.AnalystId);
            Assert.Equal("seed reason", assignment.Reason);
            Assert.Empty(await verify.AuditLogs.Where(item => item.RequestId == requestId).ToListAsync());
            await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER [dbo].[TR_TestRejectAuditInsert]");

            var result = await new AnalystAssignmentOperationService(verify)
                .AssignAsync(requestId, "operator-42", "Change succeeds with audit");
            Assert.True(result.Changed);
            Assert.Equal(1, await verify.AuditLogs.CountAsync(item => item.RequestId == requestId));
        }
    }
}
