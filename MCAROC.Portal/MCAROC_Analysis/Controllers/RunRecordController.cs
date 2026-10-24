using System.Text;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.E2e;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>A request's E2E run record (#514). Full access only. <c>?format=md</c> downloads it as Markdown for the round's artifacts and the issue.</summary>
public class RunRecordController(RunRecordService records) : Controller
{
    [HttpGet("/Requests/{id:long}/run-record")]
    public async Task<IActionResult> Index(long id, string? format, CancellationToken ct)
    {
        if (!PortalAccess.HasFullAccess(User)) return NotFound();
        var record = await records.GetAsync(id, ct);
        if (record is null) return NotFound();
        if (string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
            return File(Encoding.UTF8.GetBytes(RunRecordService.ToMarkdown(record)), "text/markdown; charset=utf-8", $"run-record-{record.RequestNumber}.md");
        return View(record);
    }
}
