using MCAROC_Analysis.Services.Chat;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>The "Ask Documents" chatbot endpoint — a plain form POST + redirect back to the request's
/// Details page, consistent with the rest of this app's server-rendered Razor style (no new JS framework).</summary>
public class ChatController(ChatService chatService) : Controller
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ask(long requestId, string question, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(question))
            await chatService.AskAsync(requestId, question.Trim(), ct);

        return RedirectToAction("Details", "Requests", new { id = requestId, area = "" });
    }
}
