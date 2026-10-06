using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.AiUsage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>What one assignment cost in model calls, by purpose, with the per-document / per-order / per-case rates that follow from it and an
/// estimate for a new assignment of a given size.</summary>
public class AiCostController(AppDbContext db, AiCostReportService reports) : Controller
{
    [HttpGet("/Requests/{id:long}/Cost")]
    public async Task<IActionResult> Index(long id, int? filings, int? orders, int? cases, int? questions, CancellationToken ct)
    {
        var company = await db.Requests.AsNoTracking().Where(r => r.RequestId == id).Select(r => r.CompanyName).FirstOrDefaultAsync(ct);
        if (company is null) return NotFound();

        var report = await reports.GetAsync(id, ct);
        var rates = await reports.UnitRatesAsync(ct);
        // The what-if form starts from this assignment's own size
        var volumes = new AiCostVolumes(
            Math.Max(0, filings ?? report.Volumes.FilingDocuments), Math.Max(0, orders ?? report.Volumes.Orders), Math.Max(0, cases ?? report.Volumes.Cases),
            Math.Max(0, questions ?? report.Volumes.Questions));
        return View(new AiCostPage(company, report, rates, volumes, AiCostReportService.Estimate(volumes, rates)));
    }
}

public sealed record AiCostPage(string CompanyName, AiCostReport Report, AiUnitRates Rates, AiCostVolumes EstimateVolumes, AiCostEstimate Estimate);
