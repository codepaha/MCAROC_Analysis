namespace MCAROC_Analysis.Data.Entities;

/// <summary>One reused chat conversation per request — Phase 4 v1 keeps a single session per request
/// (chatbot.md's own "Current Request only" scoping for a first slice); multiple named sessions are a
/// documented future candidate.</summary>
public class ChatSession
{
    public long ChatSessionId { get; set; }
    public long RequestId { get; set; }

    public DateTime CreatedDate { get; set; }
    public DateTime LastActivityDate { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}
