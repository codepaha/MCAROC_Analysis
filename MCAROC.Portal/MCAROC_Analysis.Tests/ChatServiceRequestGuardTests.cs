using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>AskAsync must reject an unknown requestId before it writes anything (real .\SQLEXPRESS test DB).</summary>
public class ChatServiceRequestGuardTests : IAsyncLifetime
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
    public async Task AskAsync_returns_null_and_writes_nothing_for_an_unknown_request()
    {
        await using var db = CreateContext();
        // The retrieval/completion collaborators are never reached for an unknown id.
        var service = new ChatService(db, null!, null!, NullLogger<ChatService>.Instance);

        const long missingRequestId = -12345;
        var result = await service.AskAsync(missingRequestId, "Who are the directors?", CancellationToken.None);

        Assert.Null(result);
        Assert.False(await db.ChatSessions.AnyAsync(s => s.RequestId == missingRequestId));
        Assert.False(await db.ChatMessages.AnyAsync(m => m.MessageText == "Who are the directors?"));
    }
}
