using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

[Authorize(Policy = AnalystAccessConstants.Policy)]
public sealed class AnalystDashboardController(AnalystDashboardQueryService queryService) : Controller
{
    [HttpGet("/analyst")]
    public async Task<IActionResult> Index(string? search, RequestStatus? status, int page = 1, CancellationToken ct = default)
    {
        var model = await queryService.BuildAsync(User, search, status, page, ct);
        return model is null ? Forbid(AnalystAccessConstants.AuthenticationScheme) : View(model);
    }
}
