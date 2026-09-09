using MCAROC_Analysis.Controllers;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MCAROC_Analysis.Tests;

/// <summary>ChatController.Ask id-validation paths (real .\SQLEXPRESS test DB). The retrieval/completion
/// collaborators are null: they're only reached for a valid id + non-blank question, which these tests
/// don't exercise.</summary>
public class ChatControllerTests : IAsyncLifetime
{
    private const string ConnectionString = @"Server=.\SQLEXPRESS;Database=MCAROC_Analysis_Test;Trusted_Connection=True;TrustServerCertificate=True;";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(ConnectionString).Options);

    private static ChatController NewController(AppDbContext db) =>
        new(new ChatService(db, null!, null!, NullLogger<ChatService>.Instance));

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
            RequestNumber = $"CCT-{Guid.NewGuid():N}",
            RequestStatus = RequestStatus.AnalysisCompleted,
            CreatedDate = DateTime.UtcNow
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        return request.RequestId;
    }

    [Fact]
    public async Task Unknown_request_with_a_blank_question_returns_NotFound()
    {
        await using var db = CreateContext();
        var result = await NewController(db).Ask(-4242, "   ", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Unknown_request_with_a_real_question_returns_NotFound()
    {
        await using var db = CreateContext();
        var result = await NewController(db).Ask(-4243, "Who are the directors?", CancellationToken.None);
        Assert.IsType<NotFoundResult>(result);
    }

    /// <summary>RequestExistsAsync passes (request existed at the guard) but AskAsync returns null
    /// (it was deleted before the second check) — mirrors the delete-in-the-gap race.</summary>
    private sealed class DeletedInTheGapChatService()
        : ChatService(null!, null!, null!, NullLogger<ChatService>.Instance)
    {
        public override Task<bool> RequestExistsAsync(long requestId, CancellationToken ct) => Task.FromResult(true);
        public override Task<ChatMessage?> AskAsync(long requestId, string question, CancellationToken ct) => Task.FromResult<ChatMessage?>(null);
    }

    [Fact]
    public async Task Request_deleted_between_the_guard_and_AskAsync_returns_NotFound()
    {
        var controller = new ChatController(new DeletedInTheGapChatService());

        var result = await controller.Ask(123, "Who are the directors?", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Known_request_with_a_blank_question_redirects_to_the_ask_tab_and_persists_nothing()
    {
        var requestId = await SeedRequestAsync();
        await using var db = CreateContext();

        var result = await NewController(db).Ask(requestId, "   ", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal("Details", redirect.ActionName);
        Assert.Equal("Requests", redirect.ControllerName);
        Assert.Equal("tab-ask", redirect.Fragment);
        Assert.NotNull(redirect.RouteValues);
        Assert.True(redirect.RouteValues.TryGetValue("id", out var idValue));
        Assert.Equal(requestId, idValue);

        await using var verify = CreateContext();
        Assert.False(await verify.ChatSessions.AnyAsync(s => s.RequestId == requestId));
    }
}
