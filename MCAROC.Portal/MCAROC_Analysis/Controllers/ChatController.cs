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
        // Validate the request id first, regardless of whether a question was supplied — a forged or
        // blank-question POST with a bad id must 404, not redirect into a Details page that doesn't exist.
        if (!await chatService.RequestExistsAsync(requestId, ct))
            return NotFound();

        // AskAsync re-checks and returns null if the request was deleted in the gap between the guard
        // above and here — honour that too.
        if (!string.IsNullOrWhiteSpace(question)
            && await chatService.AskAsync(requestId, question.Trim(), ct) is null)
        {
            return NotFound();
        }

        // Land back on the Documents tab, Ask Documents section (Details.cshtml deep-links #tab-<id>/<section>).
        return new RedirectToActionResult("Details", "Requests", new { id = requestId }, permanent: false, fragment: "tab-documents/ask");
    }
}
