using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.PreLoginReports;
using MCAROC_Analysis.Services.CompanyMaster;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Application-authenticated entry point for the limited-detail "pre-login" report tier.
/// Single-CIN and batch requests share one queue; editing is optional after generation.
/// History/Edit/Rerun/Download additionally remain scoped to the batch Guid (#47).
/// Mine renders the browser's localStorage history without a server-side batch enumeration.</summary>
[Authorize]
[Route("pre-login-reports")]
public sealed class PreLoginReportsController(PreLoginReportJobService jobs, AppDbContext? db = null) : Controller
{
    [HttpGet("")]
    public IActionResult Index() => View(new PreLoginReportViewModel());

    [HttpGet("mine")]
    public IActionResult Mine() => View();

    /// <summary>Bounded, indexed lookup in the local MCA master for a human to select a CIN or LLPIN.</summary>
    [HttpGet("borrower-lookup")]
    public async Task<IActionResult> BorrowerLookup(string? name, string? entityType, CancellationToken cancellationToken)
    {
        if (db is null) return StatusCode(503);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100) return BadRequest();
        var normalized = CompanyNameNormalizer.Normalize(name).NameNormalized;
        if (normalized.Length < 3) return BadRequest();
        var recordType = entityType?.Trim().ToLowerInvariant() switch
        {
            "company" or "limited" or "private limited" => CompanyMasterRecordType.Company,
            "llp" => CompanyMasterRecordType.Llp,
            "foreigncompany" or "foreign company" => CompanyMasterRecordType.Foreign,
            null or "" => (CompanyMasterRecordType?)null,
            _ => (CompanyMasterRecordType?)null
        };
        if (recordType is null && !string.IsNullOrWhiteSpace(entityType)) return BadRequest();
        async Task<List<CompanyMasterRecord>> LookupTypeAsync(CompanyMasterRecordType type, int limit)
        {
            var records = db.CompanyMasterRecords.AsNoTracking().Where(x => x.RecordType == type);
            var matches = await records
                .Where(x => x.NameNormalized != null && x.NameNormalized.StartsWith(normalized))
                .OrderBy(x => x.NameNormalized).ThenBy(x => x.Identifier).Take(limit)
                .Select(x => new CompanyMasterRecord { Name = x.Name, Identifier = x.Identifier,
                    RecordType = x.RecordType, Status = x.Status, State = x.State }).ToListAsync(cancellationToken);
            if (matches.Count == 0)
                matches = await records.Where(x => x.Name.StartsWith(name.Trim()))
                    .OrderBy(x => x.Name).ThenBy(x => x.Identifier).Take(limit)
                    .Select(x => new CompanyMasterRecord { Name = x.Name, Identifier = x.Identifier,
                        RecordType = x.RecordType, Status = x.Status, State = x.State }).ToListAsync(cancellationToken);
            return matches;
        }
        var matches = recordType is { } selectedType
            ? await LookupTypeAsync(selectedType, 8)
            : (await LookupTypeAsync(CompanyMasterRecordType.Company, 4))
                .Concat(await LookupTypeAsync(CompanyMasterRecordType.Llp, 4)).ToList();
        Response.Headers.CacheControl = "no-store";
        return Json(matches.Select(x => new { x.Name, x.Identifier, RecordType = x.RecordType.ToString(), x.Status, x.State }));
    }

    [HttpPost("assignment")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(11 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 11 * 1024 * 1024)]
    public async Task<IActionResult> CreateAssignment(IFormFile? requestFile, string? requestText, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View("Index", new PreLoginReportViewModel());
        try
        {
            var batchId = await jobs.QueueRequestAsync(requestFile, requestText, cancellationToken);
            return RedirectToAction(nameof(History), new { batch = batchId });
        }
        catch (PreLoginReportException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewBag.RequestText = requestText;
            return View("Index", new PreLoginReportViewModel());
        }
    }

    [HttpGet("{batch:guid}/{id:long}/assignment")]
    public async Task<IActionResult> Assignment(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindInBatchAsync(batch, id, cancellationToken);
        if (job is null) return NotFound();
        ViewBag.Job = job;
        return View(jobs.AssignmentData(job));
    }

    [HttpGet("{batch:guid}/{id:long}/assignment-json")]
    public async Task<IActionResult> AssignmentJson(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindInBatchAsync(batch, id, cancellationToken);
        if (job is null || jobs.AssignmentData(job)?.Assignment is not { } details) return NotFound();
        return Json(details);
    }

    [HttpGet("{batch:guid}/{id:long}/source")]
    public async Task<IActionResult> AssignmentSource(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindInBatchAsync(batch, id, cancellationToken);
        if (job is null) return NotFound();
        var data = jobs.AssignmentData(job);
        var source = BorrowerAssignmentIntake.PendingSource(job.DataJson);
        var path = data?.SourceStoragePath ?? source?.SourceStoragePath;
        if (path is null || !System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, "application/octet-stream", data?.SourceFileName ?? source?.SourceFileName ?? "request");
    }

    [HttpPost("{batch:guid}/{id:long}/assignment")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> CompleteAssignment(Guid batch, long id, string? borrowerName, string? entityType,
        string? mcaIdentifier, IFormFile? legalCasesFile, bool noCasesConfirmed, bool entityTypeConfirmed, CancellationToken cancellationToken)
    {
        try { await jobs.CompleteAssignmentAsync(batch, id, borrowerName, entityType, mcaIdentifier, legalCasesFile, noCasesConfirmed, entityTypeConfirmed, cancellationToken); }
        catch (PreLoginReportException ex) { TempData["ReportError"] = ex.Message; return RedirectToAction(nameof(Assignment), new { batch, id }); }
        return RedirectToAction(nameof(History), new { batch });
    }

    [HttpPost("")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Fetch(PreLoginReportViewModel model, CancellationToken cancellationToken)
    {
        model.Cin = (model.Cin ?? string.Empty).Trim().ToUpperInvariant();
        if (model.EntityType is not { } entityType || !Enum.IsDefined(entityType) || !model.EntityTypeConfirmed)
            ModelState.AddModelError(nameof(model.EntityType), "Select and confirm the borrower's legal entity type.");
        if (!ModelState.IsValid) return View("Index", model);

        try
        {
            if (!model.IsLitigationOnly && string.IsNullOrWhiteSpace(model.Cin))
            {
                var match = await jobs.ResolveExactCorporateNameAsync(model.CompanyName, model.EntityType!.Value, cancellationToken);
                model.Cin = match.Identifier;
                model.CompanyName = match.Name;
            }
            if ((model.EntityType == PreLoginReportEntityType.Company && model.Cin.Contains('-')) ||
                (model.EntityType == PreLoginReportEntityType.Llp && !model.Cin.Contains('-')))
                throw new PreLoginReportException("The confirmed entity type does not match the CIN / LLPIN.");
            var batchId = model.IsLitigationOnly
                ? await jobs.QueuePartnershipAsync(model, cancellationToken)
                : await jobs.QueueSingleAsync(model.Cin, model.CompanyName, model.Format, cancellationToken);
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
        if (model.EntityType != PreLoginReportEntityType.Company || !model.EntityTypeConfirmed)
        {
            ModelState.AddModelError(nameof(model.EntityType), "Confirm that every identifier in this batch belongs to a company.");
            return View("Index", model);
        }
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

    [HttpGet("{batch:guid}/{id:long}/preview")]
    public async Task<IActionResult> Preview(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await jobs.FindInBatchAsync(batch, id, cancellationToken);
        if (job is null || job.Status != PreLoginReportJobStatus.Completed) return NotFound();
        var data = jobs.AssignmentData(job);
        if (data is null) return NotFound();
        ViewBag.Job = job;
        ViewBag.DownloadAvailable = !string.IsNullOrWhiteSpace(job.ReportStoragePath) && System.IO.File.Exists(job.ReportStoragePath);
        return View(data);
    }

    [HttpPost("{batch:guid}/{id:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid batch, long id, PreLoginReportDraftViewModel model, IFormFile? legalCasesFile, CancellationToken cancellationToken)
    {
        model.BatchId = batch; // the route (not the posted form) is the trusted source of the access token
        if (!ModelState.IsValid) return View(model);
        try
        {
            await jobs.ApplyEditAndRegenerateAsync(batch, id, model, cancellationToken, legalCasesFile);
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
        if (job is null || job.Status != PreLoginReportJobStatus.Completed) return NotFound();
        if (string.IsNullOrWhiteSpace(job.ReportStoragePath) || !System.IO.File.Exists(job.ReportStoragePath))
        {
            TempData["ReportError"] = "The generated Word file is unavailable. The report data can still be reviewed below; rerun the assignment to regenerate the file.";
            return RedirectToAction(nameof(Preview), new { batch, id });
        }
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(job.ReportStoragePath, "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            Path.GetFileName(job.ReportStoragePath).Split('-', 2).Last());
    }
}
