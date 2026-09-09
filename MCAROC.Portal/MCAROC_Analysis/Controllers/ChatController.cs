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
        {
            var answer = await chatService.AskAsync(requestId, question.Trim(), ct);
            if (answer is null)
                return NotFound();
        }

        // Land back on the "Ask Documents" tab (Details.cshtml deep-links #tab-... hashes).
        return Redirect(Url.Action("Details", "Requests", new { id = requestId, area = "" }) + "#tab-ask");
    }
}
