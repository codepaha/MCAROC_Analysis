using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>The standalone page of one litigation case: identity, age, baseline risk tier, where the case stands, parties, order
/// timeline, the charge it bears on and the cases it shares a party with. Linkable from the charge drawer, the chat and the reports.</summary>
public class LitigationCaseController(LitigationCasePageService pages) : Controller
{
    [HttpGet("/Requests/{id:long}/Litigation/Cases/{caseId:long}")]
    public async Task<IActionResult> Detail(long id, long caseId, CancellationToken ct)
    {
        var page = await pages.GetAsync(id, caseId, ct);
        if (page is not null) return View(page);
        // A link to a case that was merged into another keeps working: it goes to the survivor (a temporary redirect, because a merge can be reversed).
        return await pages.GetSurvivorIdAsync(id, caseId, ct) is { } survivor
            ? RedirectToAction(nameof(Detail), new { id, caseId = survivor })
            : NotFound();
    }
}
