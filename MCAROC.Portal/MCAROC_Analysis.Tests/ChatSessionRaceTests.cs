using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>The one-session-per-request race (real .\SQLEXPRESS test DB). AskAsync is run with null
/// retrieval/completion collaborators: the try/catch turns the resulting failure into a persisted
/// "Failed" assistant turn, which is all these tests need — they assert on session/turn bookkeeping.</summary>
public class ChatSessionRaceTests : IAsyncLifetime
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

    private static async Task<long> SeedRequestAsync()
    {
        await using var db = CreateContext();
        var request = new McaRequest
        {
            ClientId = 1,
            EntityType = EntityType.Company,
            CompanyName = "Test Co",
            RequestNumber = $"CSR-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task Concurrent_AskAsync_for_one_request_share_a_single_session_and_persist_every_turn()
    {
        var requestId = await SeedRequestAsync();

        async Task Ask(string question)
        {
            await using var db = CreateContext();
            var service = new ChatService(db, null!, null!, NullLogger<ChatService>.Instance);
            await service.AskAsync(requestId, question, CancellationToken.None);
        }

        await Task.WhenAll(Ask("First question?"), Ask("Second question?"));

        await using var verify = CreateContext();
        var session = Assert.Single(await verify.ChatSessions.Where(s => s.RequestId == requestId).ToListAsync());
        var messages = await verify.ChatMessages.Where(m => m.ChatSessionId == session.ChatSessionId).ToListAsync();
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.User));
        Assert.Equal(2, messages.Count(m => m.Role == ChatRole.Assistant));
    }

    [Fact]
    public async Task GetOrCreateSessionAsync_rethrows_a_non_unique_DbUpdateException()
    {
        await using var db = CreateContext();
        var service = new ChatService(db, null!, null!, NullLogger<ChatService>.Instance);

        // No McaRequest with this id -> the ChatSession -> Requests FK is violated (SQL error 547),
        // which is not the unique-index race the catch handles, so it must propagate.
        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.GetOrCreateSessionAsync(-987654321, CancellationToken.None));
    }
}
