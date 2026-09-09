using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Drives one chat turn: persist the question, build retrieval context, call Gemini, validate,
/// persist the answer. Runs synchronously within the HTTP request — a chat question needs an answer now,
/// unlike Phase 2/3's fire-and-forget background pipelines.</summary>
public class ChatService(
    AppDbContext db,
    RetrievalContextBuilder contextBuilder,
    ChatCompletionService completionService,
    ILogger<ChatService> logger)
{
    private static readonly ChatRetrievalOptions Options = ChatRetrievalOptions.Default;

    public virtual Task<bool> RequestExistsAsync(long requestId, CancellationToken ct) =>
        db.Requests.AnyAsync(r => r.RequestId == requestId, ct);

    /// <summary>Returns the persisted assistant message, or <c>null</c> if <paramref name="requestId"/>
    /// matches no request. The existence check runs before any write, so a bad id never creates an
    /// orphan session (the controller also guards up front, unconditionally).</summary>
    public virtual async Task<ChatMessage?> AskAsync(long requestId, string question, CancellationToken ct)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null)
            return null;

        var session = await GetOrCreateSessionAsync(requestId, ct);

        db.ChatMessages.Add(new ChatMessage
        {
            ChatSessionId = session.ChatSessionId, Role = ChatRole.User, MessageText = question, CreatedDate = DateTime.UtcNow
        });
        session.LastActivityDate = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        // Fresh retrieval every turn — prior assistant messages are conversational context only, never
        // treated as evidence, so history is loaded purely to help the model interpret a follow-up like
        // "what about the other director?", not to answer from.
        var priorHistory = await db.ChatMessages
            .Where(m => m.ChatSessionId == session.ChatSessionId)
            .OrderByDescending(m => m.CreatedDate)
            .Skip(1) // the question just added above
            .Take(Options.ChatHistoryTurnLimit)
            .ToListAsync(ct);
        priorHistory.Reverse();

        ChatMessage assistantMessage;
        try
        {
            var context = await contextBuilder.BuildAsync(requestId, question, ct);
            var completion = await completionService.CompleteAsync(request.CompanyName, context, priorHistory, question, ct);

            assistantMessage = new ChatMessage
            {
                ChatSessionId = session.ChatSessionId,
                Role = ChatRole.Assistant,
                MessageText = completion.Answer,
                RetrievedSourcesJson = JsonSerializer.Serialize(context.Sources.Select(s => new
                {
                    s.Tag, Type = s.Type.ToString(), s.DisplayLabel, s.RelevanceScore
                })),
                CitedSourcesJson = JsonSerializer.Serialize(completion.CitedSources),
                Model = ChatCompletionService.ModelId,
                PromptVersion = ChatCompletionService.PromptVersion,
                EmbeddingModel = EmbeddingService.ModelId,
                TopK = Options.TopK,
                Status = ChatMessageStatus.Success,
                CreatedDate = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat completion failed for request {RequestId}", requestId);
            assistantMessage = new ChatMessage
            {
                ChatSessionId = session.ChatSessionId,
                Role = ChatRole.Assistant,
                MessageText = "Sorry, something went wrong answering that question. Please try again.",
                Status = ChatMessageStatus.Failed,
                CreatedDate = DateTime.UtcNow
            };
        }

        db.ChatMessages.Add(assistantMessage);
        await db.SaveChangesAsync(ct);
        return assistantMessage;
    }

    private async Task<ChatSession> GetOrCreateSessionAsync(long requestId, CancellationToken ct)
    {
        var existing = await db.ChatSessions.FirstOrDefaultAsync(s => s.RequestId == requestId, ct);
        if (existing is not null)
            return existing;

        var session = new ChatSession { RequestId = requestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }
}
