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
        return page is null ? NotFound() : View(page);
    }
}
