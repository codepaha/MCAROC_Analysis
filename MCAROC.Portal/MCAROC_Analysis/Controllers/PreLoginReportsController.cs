using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>Public, credential-free entry point for the two MCA report formats.</summary>
[AllowAnonymous]
[Route("pre-login-reports")]
public sealed class PreLoginReportsController(PreLoginReportService reports, PreLoginReportJobService jobs) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(new PreLoginReportViewModel());

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(PreLoginReportViewModel model, CancellationToken cancellationToken)
    {
        model.Cin = (model.Cin ?? string.Empty).Trim().ToUpperInvariant();
        if (!ModelState.IsValid) return View("Index", model);

        try
        {
            return View("Review", await reports.PrepareDraftAsync(model, cancellationToken));
        }
        catch (PreLoginReportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Index", model);
        }
    }

    [HttpPost("generate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Generate(PreLoginReportDraftViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View("Review", model);
        try
        {
            var document = await reports.GenerateDraftAsync(model, cancellationToken);
            return File(document.Bytes, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", document.FileName);
        }
        catch (PreLoginReportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Review", model);
        }
    }

    [HttpPost("batch")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Batch(PreLoginReportViewModel model, CancellationToken cancellationToken)
    {
        try
        {
            var batchId = await jobs.QueueBatchAsync((model.Cins ?? string.Empty).Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries), model.Format, cancellationToken);
            return RedirectToAction(nameof(History), new { batch = batchId });
        }
        catch (PreLoginReportException ex) { ModelState.AddModelError(string.Empty, ex.Message); return View("Index", model); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] Guid? batch, CancellationToken cancellationToken)
    {
        ViewBag.Batch = batch;
        return View(await jobs.HistoryAsync(cancellationToken));
    }

    [HttpPost("{id:long}/rerun")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rerun(long id, CancellationToken cancellationToken)
    {
        try { await jobs.RerunAsync(id, cancellationToken); }
        catch (PreLoginReportException ex) { TempData["ReportError"] = ex.Message; }
        return RedirectToAction(nameof(History));
    }

    [HttpGet("{id:long}/download")]
    public async Task<IActionResult> Download(long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, cancellationToken);
        if (job is null || job.Status != MCAROC_Analysis.Data.Entities.PreLoginReportJobStatus.Completed || string.IsNullOrWhiteSpace(job.ReportStoragePath) || !System.IO.File.Exists(job.ReportStoragePath)) return NotFound();
        return File(await System.IO.File.ReadAllBytesAsync(job.ReportStoragePath, cancellationToken), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Path.GetFileName(job.ReportStoragePath).Split('-', 2).Last());
    }
}
