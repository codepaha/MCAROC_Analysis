using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Tests;

/// <summary>Integration test for the unique index on ChatSession.RequestId (real .\SQLEXPRESS test DB).</summary>
public class ChatSessionUniquenessTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Second_ChatSession_for_the_same_request_is_rejected()
    {
        await using var db = CreateContext();

        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Test Co",
            RequestNumber = $"CSU-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();

        db.ChatSessions.Add(new ChatSession { RequestId = request.RequestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        db.ChatSessions.Add(new ChatSession { RequestId = request.RequestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
