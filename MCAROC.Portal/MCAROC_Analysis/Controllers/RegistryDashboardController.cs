using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Services.Registry;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

[Authorize(AuthenticationSchemes = "InternalReviewer")]
[Route("registry")]
public sealed class RegistryDashboardController : Controller
{
    private readonly CompanyRegistryQueryService _queryService;

    public RegistryDashboardController(CompanyRegistryQueryService queryService)
    {
        _queryService = queryService;
    }

    [HttpGet("")]
    [HttpGet("dashboard")]
    public async Task<IActionResult> Index(
        [FromQuery] string? tab = null,
        [FromQuery] RegistryExplorerCriteria? explorer = null,
        CancellationToken ct = default)
    {
        var model = await _queryService.GetDashboardAsync(tab, explorer, ct);
        return RenderDashboard(model);
    }

    [HttpGet("explorer")]
    public async Task<IActionResult> Explorer(
        [FromQuery] RegistryExplorerCriteria criteria,
        CancellationToken ct = default)
    {
        var model = await _queryService.GetDashboardAsync("explorer", criteria, ct);
        return RenderDashboard(model);
    }
    private IActionResult RenderDashboard(RegistryDashboardViewModel model)
    {
        if (model.State == RegistrySnapshotState.DatabaseUnavailable)
        {
            Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status503ServiceUnavailable;
            Response.Headers["Retry-After"] = "30";
        }
        else if (model.Explorer?.ValidationErrorMessage != null)
        {
            Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status400BadRequest;
        }
        return View("~/Views/Registry/Index.cshtml", model);
    }
}

