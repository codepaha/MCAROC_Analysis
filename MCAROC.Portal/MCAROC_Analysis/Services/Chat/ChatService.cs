using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Drives one chat turn: persist the question, build retrieval context, call Gemini, validate,
/// persist the answer. Runs synchronously within the HTTP request — a chat question needs an answer now,
/// unlike Phase 2/3's fire-and-forget background pipelines.</summary>
public enum ChatTurnOutcome
{
    Success,
    RequestNotFound,
    UpstreamFailure
}

public record ChatTurnResult(ChatTurnOutcome Outcome, ChatMessage? Message = null);

public class ChatService(
    AppDbContext db,
    RetrievalContextBuilder contextBuilder,
    ChatCompletionService completionService,
    ILogger<ChatService> logger)
{
    private static readonly ChatRetrievalOptions Options = ChatRetrievalOptions.Default;

    public virtual Task<bool> RequestExistsAsync(long requestId, CancellationToken ct) =>
        db.Requests.AnyAsync(r => r.RequestId == requestId, ct);

    public virtual async Task<ChatMessage?> AskAsync(long requestId, string question, CancellationToken ct)
    {
        var result = await AskTurnAsync(requestId, question, ct);
        return result.Message;
    }

    /// <summary>Drives one chat turn with typed outcomes for the API layer: persists the question,
    /// calls completion, and handles cancellation (rollback) vs upstream failure (audit persistence)
    /// cleanly without leaking exception details.</summary>
    public virtual async Task<ChatTurnResult> AskTurnAsync(long requestId, string question, CancellationToken ct)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null)
            return new ChatTurnResult(ChatTurnOutcome.RequestNotFound);

        var session = await GetOrCreateSessionAsync(requestId, ct);

        var userMessage = new ChatMessage
        {
            ChatSessionId = session.ChatSessionId, Role = ChatRole.User, MessageText = question, CreatedDate = DateTime.UtcNow
        };
        db.ChatMessages.Add(userMessage);
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
            db.ChatMessages.Add(assistantMessage);
            await db.SaveChangesAsync(ct);
            return new ChatTurnResult(ChatTurnOutcome.Success, assistantMessage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Client aborted or timeout: roll back user message so no orphaned turn remains
            db.ChatMessages.Remove(userMessage);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
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
            db.ChatMessages.Add(assistantMessage);
            await db.SaveChangesAsync(CancellationToken.None);
            return new ChatTurnResult(ChatTurnOutcome.UpstreamFailure, assistantMessage);
        }
    }

    // 2601 = duplicate key in a unique index; 2627 = unique/primary-key constraint violation.
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    /// <summary>Test seam: runs in GetOrCreateSessionAsync between the "does a session exist?" read and
    /// the insert, so a test can hold two callers there until both have passed the read and force the
    /// insert race. No-op in production.</summary>
    internal virtual Task AfterSessionExistenceCheckAsync(CancellationToken ct) => Task.CompletedTask;

    internal async Task<ChatSession> GetOrCreateSessionAsync(long requestId, CancellationToken ct)
    {
        var existing = await db.ChatSessions.FirstOrDefaultAsync(s => s.RequestId == requestId, ct);
        if (existing is not null)
            return existing;

        await AfterSessionExistenceCheckAsync(ct);

        var session = new ChatSession { RequestId = requestId, CreatedDate = DateTime.UtcNow, LastActivityDate = DateTime.UtcNow };
        db.ChatSessions.Add(session);
        try
        {
            await db.SaveChangesAsync(ct);
            return session;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException
                   { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation })
        {
            // A concurrent request won the race to create the one-per-request session (unique index on
            // RequestId). Drop our losing insert and use theirs so both turns land in one conversation.
            // Any other DbUpdateException (deadlock, timeout, FK failure, ...) propagates.
            db.Entry(session).State = EntityState.Detached;
            return await db.ChatSessions.FirstAsync(s => s.RequestId == requestId, ct);
        }
    }
}
