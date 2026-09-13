using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>Public, credential-free entry point for the two MCA report formats. "Pre-login" names a
/// REPORT TIER (a limited-detail report — company profile, open charges, director names — analogous to
/// a reference tool's free/unlocked tabs), not an authentication checkpoint: this application has no
/// user-login system anywhere, for this pipeline or the main portal's own (fuller, "post-login"-tier)
/// reports. Single-CIN and batch requests share one pipeline (both just queue a job) — editing is an
/// optional, post-hoc action from History, not a mandatory gate before generation. Because there is no
/// login ANYWHERE in this app (not because this is "the part before login"), every action past initial
/// submission (History/Edit/Rerun/Download) is scoped to a <c>batch</c> Guid route segment, which serves
/// as this pipeline's only access credential (#47) — see <see cref="PreLoginReportJobService.FindInBatchAsync"/>.
/// <see cref="Mine"/> is the one exception worth calling out explicitly: it renders a static shell over
/// the browser's own <c>localStorage</c> and performs no server-side batch lookup at all, so "my past
/// reports" stays a per-browser client concern rather than reintroducing a server-side listing.</summary>
[AllowAnonymous]
[Route("pre-login-reports")]
public sealed class PreLoginReportsController(PreLoginReportJobService jobs) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(new PreLoginReportViewModel());

    [HttpGet("mine")]
    public IActionResult Mine() => View();

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(PreLoginReportViewModel model, CancellationToken cancellationToken)
    {
        model.Cin = (model.Cin ?? string.Empty).Trim().ToUpperInvariant();
        if (!ModelState.IsValid) return View("Index", model);

        try
        {
            var batchId = await jobs.QueueSingleAsync(model.Cin, model.CompanyName, model.Format, cancellationToken);
            return RedirectToAction(nameof(History), new { batch = batchId });
        }
        catch (PreLoginReportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View("Index", model);
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

    /// <summary>There is no login on this pipeline, so <paramref name="batch"/> (an unguessable Guid, never
    /// listed anywhere) IS the access credential — the only way to reach this page is the redirect right
    /// after submitting, or a bookmarked/shared link. #47: this used to be a bare, unscoped "every job in
    /// the system" list reachable by anyone.</summary>
    [HttpGet("{batch:guid}/history")]
    public async Task<IActionResult> History(Guid batch, CancellationToken cancellationToken)
    {
        ViewBag.Batch = batch;
        return View(await jobs.HistoryAsync(batch, cancellationToken));
    }

    [HttpGet("{batch:guid}/{id:long}/edit")]
    public async Task<IActionResult> Edit(Guid batch, long id, CancellationToken cancellationToken)
    {
        try { return View(await jobs.GetEditableDraftAsync(batch, id, cancellationToken)); }
        catch (PreLoginReportException ex) { TempData["ReportError"] = ex.Message; return RedirectToAction(nameof(History), new { batch }); }
    }

    [HttpPost("{batch:guid}/{id:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid batch, long id, PreLoginReportDraftViewModel model, CancellationToken cancellationToken)
    {
        model.BatchId = batch; // the route (not the posted form) is the trusted source of the access token
        if (!ModelState.IsValid) return View(model);
        try
        {
            await jobs.ApplyEditAndRegenerateAsync(batch, id, model, cancellationToken);
            return RedirectToAction(nameof(History), new { batch });
        }
        catch (PreLoginReportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return View(model);
        }
    }

    [HttpPost("{batch:guid}/{id:long}/rerun")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rerun(Guid batch, long id, CancellationToken cancellationToken)
    {
        try { await jobs.RerunAsync(batch, id, cancellationToken); }
        catch (PreLoginReportException ex) { TempData["ReportError"] = ex.Message; }
        return RedirectToAction(nameof(History), new { batch });
    }

    [HttpGet("{batch:guid}/{id:long}/download")]
    public async Task<IActionResult> Download(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindInBatchAsync(batch, id, cancellationToken);
        if (job is null || job.Status != MCAROC_Analysis.Data.Entities.PreLoginReportJobStatus.Completed || string.IsNullOrWhiteSpace(job.ReportStoragePath) || !System.IO.File.Exists(job.ReportStoragePath)) return NotFound();
        return File(await System.IO.File.ReadAllBytesAsync(job.ReportStoragePath, cancellationToken), "application/vnd.openxmlformats-officedocument.wordprocessingml.document", Path.GetFileName(job.ReportStoragePath).Split('-', 2).Last());
    }
}
