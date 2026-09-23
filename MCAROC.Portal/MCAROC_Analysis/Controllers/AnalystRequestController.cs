using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

[Authorize(Policy = AnalystAccessConstants.Policy)]
public sealed class AnalystRequestController(AnalystRequestDetailsQueryService detailsQuery) : Controller
{
    [HttpGet("/analyst/requests/{id:long}")]
    public async Task<IActionResult> Details(long id, CancellationToken ct)
    {
        var model = await detailsQuery.BuildAsync(User, id, ct);
        return model is null ? NotFound() : View(model);
    }
}
