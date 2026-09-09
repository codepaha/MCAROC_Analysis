using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

public class DashboardController(DashboardQueryService queryService) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index([FromQuery] DashboardFilterCriteria filters)
    {
        var vm = await queryService.BuildAsync(filters);
        return View(vm);
    }
}
