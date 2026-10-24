using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Preflight;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>The pre-flight check (#514), run at the start of every E2E round. Full access only: it shows integration errors and configuration.
/// <c>?format=json</c> returns the same report for scripts.</summary>
public class PreflightController(PreflightService preflight) : Controller
{
    [HttpGet("/Preflight")]
    public async Task<IActionResult> Index(string? format, CancellationToken ct)
    {
        if (!PortalAccess.HasFullAccess(User)) return NotFound();
        var report = await preflight.RunAsync(ct);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return Json(new
            {
                checkedUtc = report.CheckedUtc,
                overall = report.Overall.ToString(),
                checks = report.Checks.Select(c => new { c.Key, c.Name, outcome = c.Outcome.ToString(), c.Detail, c.Fix, c.ElapsedMs })
            });
        return View(report);
    }
}
