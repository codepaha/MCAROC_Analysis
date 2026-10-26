using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services;
using MCAROC_Analysis.Services.LitigationModule.PreLogin;
using MCAROC_Analysis.Services.PreLoginReports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

[Authorize]
[Route("assignments")]
public sealed class AssignmentsController(
    AppDbContext db,
    PreLoginReportJobService jobs,
    PreLoginAttributionService attribution) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? status, string? search, CancellationToken ct)
    {
        var query = db.PreLoginReportJobs.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<PreLoginReportJobStatus>(status, true, out var parsedStatus))
        {
            query = query.Where(j => j.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(j => j.Cin.Contains(term) || (j.SubmittedCompanyName != null && j.SubmittedCompanyName.Contains(term)));
        }

        var jobsList = await query.OrderByDescending(j => j.CreatedUtc).Take(100).ToListAsync(ct);
        var items = new List<AssignmentListingItemViewModel>();

        foreach (var job in jobsList)
        {
            var stored = PreLoginReportJobService.DeserializeStoredData(job.DataJson);
            var assignment = stored?.Assignment;
            var borrowerName = job.SubmittedCompanyName ?? stored?.Company?.Name ?? assignment?.CompanyDetails?.CompanyName ?? job.Cin;
            var entityType = stored?.Company?.EntityType ?? assignment?.CompanyDetails?.EntityType ?? "Company";
            var branchName = assignment?.RequestingBranchDetails?.BranchName ?? "-";
            var branchCode = assignment?.RequestingBranchDetails?.BranchCode ?? "-";
            var casesCount = stored?.LegalCases?.Cases?.Count ?? 0;
            var noLit = stored?.LegalCases is not null && casesCount == 0;

            items.Add(new AssignmentListingItemViewModel
            {
                PreLoginReportJobId = job.PreLoginReportJobId,
                BatchId = job.BatchId,
                Identifier = job.Cin,
                BorrowerName = borrowerName,
                EntityType = entityType,
                BranchName = branchName,
                BranchCode = branchCode,
                Status = job.Status.ToString(),
                ProgressPercent = job.ProgressPercent,
                TotalCases = casesCount,
                NoLitigationFound = noLit,
                CreatedUtc = job.CreatedUtc,
                ReportStoragePath = job.ReportStoragePath
            });
        }

        return View(new AssignmentListingViewModel
        {
            Items = items,
            SelectedStatus = status,
            SearchText = search
        });
    }

    [HttpGet("view/{batch:guid}/{id:long}")]
    public async Task<IActionResult> ViewAssignment(Guid batch, long id, CancellationToken ct)
    {
        var job = await jobs.GetAssignmentWithTargetsAsync(batch, id, ct);
        if (job is null) return NotFound();

        var stored = PreLoginReportJobService.DeserializeStoredData(job.DataJson);
        var assignment = stored?.Assignment;
        var borrowerName = job.SubmittedCompanyName ?? stored?.Company?.Name ?? assignment?.CompanyDetails?.CompanyName ?? job.Cin;
        var entityType = stored?.Company?.EntityType ?? assignment?.CompanyDetails?.EntityType ?? "Company";
        var borrowerTarget = job.LitigationTargets?.FirstOrDefault(t => t.TargetKind == PreLoginTargetKind.Borrower && t.TargetIndex == 0);
        var directorTargets = job.LitigationTargets?.Where(t => t.TargetKind == PreLoginTargetKind.Director).ToList() ?? [];

        var caseAttributions = await attribution.GetCaseAttributionsForJobAsync(job.PreLoginReportJobId, ct);

        var viewModel = new AssignmentDetailViewModel
        {
            PreLoginReportJobId = job.PreLoginReportJobId,
            BatchId = job.BatchId,
            Cin = job.Cin,
            BorrowerName = borrowerName,
            EntityType = entityType,
            Status = job.Status.ToString(),
            ProgressPercent = job.ProgressPercent,
            DataVersion = job.DataVersion,
            FailureReason = job.FailureReason,
            CreatedUtc = job.CreatedUtc,
            ReportStoragePath = job.ReportStoragePath,
            Assignment = assignment,
            StoredData = stored,
            BorrowerTarget = borrowerTarget,
            DirectorTargets = directorTargets,
            Cases = stored?.LegalCases?.Cases ?? [],
            CaseAttributions = caseAttributions
        };

        return View(viewModel);
    }

    [HttpPost("search-director/{batch:guid}/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SearchDirector(Guid batch, long id, string directorName, string? dinPan, int targetIndex, CancellationToken ct)
    {
        try
        {
            await jobs.SearchDirectorAsync(batch, id, directorName, dinPan, targetIndex, ct);
            TempData["SuccessMessage"] = $"Litigation search initiated for director {directorName}.";
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(ViewAssignment), new { batch, id });
    }

    [HttpPost("provide-vendor-job-id/{batch:guid}/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ProvideVendorJobId(Guid batch, long id, long targetId, string vendorJobId, string? evidenceNote, int submittedAttemptVersion, CancellationToken ct)
    {
        try
        {
            await jobs.ProvideVendorJobIdAsync(batch, id, targetId, vendorJobId, evidenceNote, submittedAttemptVersion, ct);
            TempData["SuccessMessage"] = "Vendor job ID accepted. Polling resumed with renewed timeout.";
        }
        catch (PreLoginConcurrencyException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(ViewAssignment), new { batch, id });
    }

    [HttpPost("confirm-no-search/{batch:guid}/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmNoSearch(Guid batch, long id, long targetId, string evidenceNote, int submittedAttemptVersion, CancellationToken ct)
    {
        try
        {
            await jobs.ConfirmNoSearchExistsAsync(batch, id, targetId, evidenceNote, submittedAttemptVersion, ct);
            TempData["SuccessMessage"] = "Target reset cleanly for fresh registration.";
        }
        catch (PreLoginConcurrencyException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(ViewAssignment), new { batch, id });
    }

    [HttpPost("repoll/{batch:guid}/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Repoll(Guid batch, long id, long targetId, int submittedAttemptVersion, CancellationToken ct)
    {
        try
        {
            await jobs.RepollAsync(batch, id, targetId, submittedAttemptVersion, ct);
            TempData["SuccessMessage"] = "Re-poll initiated with renewed timeout window.";
        }
        catch (PreLoginConcurrencyException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(ViewAssignment), new { batch, id });
    }

    [HttpPost("retry/{batch:guid}/{id:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid batch, long id, long targetId, int submittedAttemptVersion, CancellationToken ct)
    {
        try
        {
            await jobs.RetryCleanFailureAsync(batch, id, targetId, submittedAttemptVersion, ct);
            TempData["SuccessMessage"] = "Clean retry initiated.";
        }
        catch (PreLoginConcurrencyException ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
        }

        return RedirectToAction(nameof(ViewAssignment), new { batch, id });
    }

    [HttpPost("single-case-edit/{batch:guid}/{id:long}")]
    public async Task<IActionResult> SingleCaseEdit(Guid batch, long id, [FromBody] SingleCaseEditRequest request, CancellationToken ct)
    {
        try
        {
            var payload = new SingleCaseEditPayload(
                request.Status,
                request.CaseStage,
                request.IsExcludedFromReport,
                request.ExclusionReason,
                request.AnalystNote);

            await jobs.SingleCaseEditAsync(batch, id, request.CaseRowId, payload, request.SubmittedDataVersion, ct);
            return Json(new { success = true, message = "Case updated successfully." });
        }
        catch (PreLoginConcurrencyException ex)
        {
            return StatusCode(409, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("batch-update-cases/{batch:guid}/{id:long}")]
    public async Task<IActionResult> BatchUpdateCases(Guid batch, long id, [FromBody] BatchUpdateCasesPayload payload, CancellationToken ct)
    {
        try
        {
            var result = await jobs.BatchUpdateCasesAsync(batch, id, payload, ct);
            return Json(result);
        }
        catch (PreLoginConcurrencyException ex)
        {
            return StatusCode(409, new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("download/{batch:guid}/{id:long}")]
    public async Task<IActionResult> Download(Guid batch, long id, CancellationToken ct)
    {
        var job = await jobs.FindInBatchAsync(batch, id, ct);
        var fullReportPath = MCAROC_Analysis.Services.Storage.StoragePathHelper.ResolveFullPath(job?.ReportStoragePath);
        if (job is null || string.IsNullOrWhiteSpace(fullReportPath) || !System.IO.File.Exists(fullReportPath))
            return NotFound("Report not found or not yet generated.");

        var fileName = Path.GetFileName(fullReportPath);
        return PhysicalFile(fullReportPath, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", fileName);
    }

    [HttpGet("suggestions")]
    public async Task<IActionResult> Suggestions(long? jobId, string? reasonKind, PreLoginTargetKind? targetKind, CancellationToken ct)
    {
        var items = await attribution.GetOpenSuggestionsAsync(jobId, reasonKind, targetKind, ct);
        var reasons = items.Select(i => i.Rule).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().OrderBy(r => r).ToList();

        var vm = new AssignmentSuggestionsViewModel
        {
            Items = items,
            SelectedJobId = jobId,
            SelectedReasonKind = reasonKind,
            SelectedTargetKind = targetKind,
            AvailableReasons = reasons
        };

        return View(vm);
    }

    [HttpGet("auto-exclusions")]
    public async Task<IActionResult> AutoExclusions(long? jobId, string? reasonKind, PreLoginTargetKind? targetKind, CancellationToken ct)
    {
        var items = await attribution.GetAutoExcludedCasesAsync(jobId, reasonKind, targetKind, ct);
        var reasons = items.Select(i => i.Rule).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().OrderBy(r => r).ToList();

        var vm = new AssignmentAutoExclusionsViewModel
        {
            Items = items,
            SelectedJobId = jobId,
            SelectedReasonKind = reasonKind,
            SelectedTargetKind = targetKind,
            AvailableReasons = reasons
        };

        return View(vm);
    }

    [HttpGet("trust-report")]
    public async Task<IActionResult> TrustReport(long? jobId, DateTime? fromDate, DateTime? toDate, DateTime? fromUtc, DateTime? toUtc, CancellationToken ct)
    {
        var effectiveFrom = fromDate ?? fromUtc;
        var effectiveTo = toDate ?? toUtc;

        DateTime? startUtc = effectiveFrom.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(effectiveFrom.Value.Date, Ist.Zone)
            : null;

        DateTime? endUtcExclusive = effectiveTo.HasValue
            ? TimeZoneInfo.ConvertTimeToUtc(effectiveTo.Value.Date.AddDays(1), Ist.Zone)
            : null;

        var report = await attribution.GetTrustReportAsync(jobId, startUtc, endUtcExclusive, ct);

        var vm = new AssignmentTrustReportViewModel
        {
            Report = report,
            SelectedJobId = jobId,
            FromDate = effectiveFrom?.Date,
            ToDate = effectiveTo?.Date,
            FromUtc = effectiveFrom?.Date,
            ToUtc = effectiveTo?.Date
        };

        return View(vm);
    }

    [HttpPost("accept-suggestion/{scopeCaseId:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptSuggestion(long scopeCaseId, string? returnUrl, CancellationToken ct)
    {
        var analyst = User.Identity?.Name ?? "Analyst";
        var ok = await attribution.AcceptSuggestionWithSyncAsync(scopeCaseId, analyst, ct);
        if (ok) TempData["SuccessMessage"] = "Suggested exclusion accepted and case excluded from report.";
        else TempData["ErrorMessage"] = "Failed to accept suggestion for the specified case.";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Suggestions));
    }

    [HttpPost("reject-suggestion/{scopeCaseId:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RejectSuggestion(long scopeCaseId, string? returnUrl, CancellationToken ct)
    {
        var analyst = User.Identity?.Name ?? "Analyst";
        var ok = await attribution.RejectSuggestionWithSyncAsync(scopeCaseId, analyst, ct);
        if (ok) TempData["SuccessMessage"] = "Suggested exclusion rejected. Case remains included in report.";
        else TempData["ErrorMessage"] = "Failed to reject suggestion for the specified case.";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(Suggestions));
    }

    [HttpPost("undo-exclusion/{scopeCaseId:long}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UndoExclusion(long scopeCaseId, string? returnUrl, CancellationToken ct)
    {
        var analyst = User.Identity?.Name ?? "Analyst";
        var ok = await attribution.UndoExclusionWithSyncAsync(scopeCaseId, analyst, ct);
        if (ok) TempData["SuccessMessage"] = "Automatic exclusion undone. Case restored to report.";
        else TempData["ErrorMessage"] = "Failed to undo automatic exclusion.";

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return Redirect(returnUrl);
        return RedirectToAction(nameof(AutoExclusions));
    }
}

public sealed class SingleCaseEditRequest
{
    public string CaseRowId { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? CaseStage { get; set; }
    public bool IsExcludedFromReport { get; set; }
    public string? ExclusionReason { get; set; }
    public string? AnalystNote { get; set; }
    public int SubmittedDataVersion { get; set; }
}

