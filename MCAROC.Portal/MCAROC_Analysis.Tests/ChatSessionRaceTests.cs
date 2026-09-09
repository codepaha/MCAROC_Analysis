using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>The one-session-per-request race (real .\SQLEXPRESS test DB).</summary>
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

    /// <summary>Holds every caller at the point between the "session exists?" read and the insert until
    /// all <c>parties</c> have arrived, so the insert race is forced rather than left to timing.</summary>
    private sealed class BarrieredChatService(AppDbContext db, Barrier barrier)
        : ChatService(db, null!, null!, NullLogger<ChatService>.Instance)
    {
        internal override Task AfterSessionExistenceCheckAsync(CancellationToken ct)
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(15));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Two_callers_that_both_pass_the_existence_check_still_converge_on_one_session()
    {
        var requestId = await SeedRequestAsync();
        using var barrier = new Barrier(2);

        async Task<long> CreateSession()
        {
            await using var db = CreateContext();
            var service = new BarrieredChatService(db, barrier);
            var session = await service.GetOrCreateSessionAsync(requestId, CancellationToken.None);
            return session.ChatSessionId;
        }

        var ids = await Task.WhenAll(Task.Run(CreateSession), Task.Run(CreateSession));

        // Same session for both, and exactly one row. Against the pre-fix implementation (non-unique
        // index, no re-query) the forced race inserts two rows and these both fail.
        Assert.Equal(ids[0], ids[1]);
        await using var verify = CreateContext();
        Assert.Single(await verify.ChatSessions.Where(s => s.RequestId == requestId).ToListAsync());
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
