using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

public sealed class AnalystAssignmentOperationServiceTests : IAsyncLifetime
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
    public async Task AssignAsync_ReassignsToTheOnlyActiveAnalyst_AndAuditsTheBeforeAndAfterState()
    {
        await using var db = CreateContext();
        await db.AnalystAssignments.ExecuteDeleteAsync();
        await db.Analysts.ExecuteDeleteAsync();

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

        var audit = new RecordingAuditLogService();
        var service = new AnalystAssignmentOperationService(db, audit);
        var result = await service.AssignAsync(request.RequestId, "operator-42", "Follow-up required");

        Assert.True(result.Changed);
        Assert.Equal(existing.AnalystAssignmentId, result.AnalystAssignmentId);
        Assert.Equal(formerAnalyst.AnalystId, result.PreviousAnalystId);
        Assert.Equal(activeAnalyst.AnalystId, result.NewAnalystId);

        var updated = await db.AnalystAssignments.SingleAsync(item => item.RequestId == request.RequestId);
        Assert.Equal(activeAnalyst.AnalystId, updated.AnalystId);
        Assert.Equal("operator-42", updated.AssignedByActorId);
        Assert.Equal("Follow-up required", updated.Reason);

        var evt = Assert.IsType<AuditEvent<AnalystAssignmentAuditPayload>>(Assert.Single(audit.Events));
        Assert.Equal(AuditActionType.AnalystAssignmentChanged, evt.Action);
        Assert.Equal(request.RequestId, evt.RequestId);
        Assert.Equal("operator-42", evt.ActorId);
        Assert.Equal(existing.AnalystAssignmentId, evt.Payload!.AnalystAssignmentId);
        Assert.Equal(formerAnalyst.AnalystId, evt.Payload.PreviousAnalystId);
        Assert.Equal(activeAnalyst.AnalystId, evt.Payload.NewAnalystId);
        Assert.Equal("Follow-up required", evt.Payload.Reason);

        var repeated = await service.AssignAsync(request.RequestId, "operator-43", "Follow-up required");
        Assert.False(repeated.Changed);
        Assert.Single(audit.Events);
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<object> Events { get; } = [];
        public Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
        public Task TryLogAsync(AuditEvent evt, CancellationToken ct = default)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }
}
