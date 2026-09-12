namespace MCAROC_Analysis.Models.Chat;

public class AskChatJsonRequest
{
    public string? Question { get; set; }
    public Guid? ClientTurnId { get; set; }
}

public class ChatCitationDto
{
    public string SourceType { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? DocumentName { get; set; }
    public int? PageNumber { get; set; }
    public long? DocumentId { get; set; }
    public string? ViewerUrl { get; set; }
}

public class ChatMessageDto
{
    public long Id { get; set; }
    public Guid? ClientTurnId { get; set; }
    public long? InReplyToChatMessageId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public List<ChatCitationDto> Citations { get; set; } = [];
}

public class ChatErrorDto
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public class ChatApiResponse
{
    public bool Success { get; set; }
    public ChatMessageDto? Message { get; set; }
    public ChatErrorDto? Error { get; set; }

    public static ChatApiResponse Fail(string code, string message, ChatMessageDto? msg = null) =>
        new() { Success = false, Error = new ChatErrorDto { Code = code, Message = message }, Message = msg };

    public static ChatApiResponse Ok(ChatMessageDto message) =>
        new() { Success = true, Message = message };
}
