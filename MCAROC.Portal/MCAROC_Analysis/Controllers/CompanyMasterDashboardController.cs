using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.CompanyMaster;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Controllers;

[Authorize(AuthenticationSchemes = "InternalReviewer")]
[Route("internal/company-master")]
public sealed class CompanyMasterDashboardController : Controller
{
    private readonly AppDbContext _db;
    private readonly ICompanyMasterDeltaService _deltaService;
    private readonly ISyncLockLease _lockLease;
    private readonly IProxyPoolService _proxyPool;
    private readonly ISafeArchiveExtractor _archiveExtractor;
    private readonly ILogger<CompanyMasterDashboardController> _logger;

    public CompanyMasterDashboardController(
        AppDbContext db,
        ICompanyMasterDeltaService deltaService,
        ISyncLockLease lockLease,
        IProxyPoolService proxyPool,
        ISafeArchiveExtractor archiveExtractor,
        ILogger<CompanyMasterDashboardController> logger)
    {
        _db = db;
        _deltaService = deltaService;
        _lockLease = lockLease;
        _proxyPool = proxyPool;
        _archiveExtractor = archiveExtractor;
        _logger = logger;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var model = await BuildViewModelAsync(cancellationToken);
        return View("~/Views/CompanyMaster/Index.cshtml", model);
    }

    [HttpPost("probe")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Probe(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manual portal probe triggered by reviewer.");
        var date = await _deltaService.ProbePortalSnapshotDateAsync(cancellationToken);

        if (date.HasValue)
        {
            TempData["SuccessMessage"] = $"Portal check successful. Latest published master snapshot date: {date.Value:dd-MMM-yyyy}.";
        }
        else
        {
            TempData["ErrorMessage"] = "Could not parse snapshot date from portal. Check proxy health or website responsiveness.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("test-proxies")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TestProxies(CancellationToken cancellationToken)
    {
        var nodes = _proxyPool.GetAllNodes();
        int tested = 0, passed = 0;

        foreach (var node in nodes)
        {
            tested++;
            if (await _proxyPool.TestNodeAsync(node, cancellationToken))
            {
                passed++;
            }
        }

        TempData["SuccessMessage"] = $"Tested {tested} proxy nodes: {passed} active/healthy, {tested - passed} failed.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("sync-now")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SyncNow(CancellationToken cancellationToken)
    {
        var lockResult = await _lockLease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.Zero, cancellationToken);
        if (!lockResult.Succeeded)
        {
            TempData["ErrorMessage"] = $"Cannot start sync: {lockResult.FailureReason}";
            return RedirectToAction(nameof(Index));
        }

        try
        {
            var job = await _deltaService.CreateJobAsync(CompanyMasterSyncTriggerType.ManualForceSync, "ReviewerManual", cancellationToken);
            TempData["SuccessMessage"] = $"Manual sync job #{job.JobId} initiated with fencing token {job.FencingToken}.";
        }
        finally
        {
            await _lockLease.ReleaseAsync(CancellationToken.None);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("upload-manual")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(524_288_000)] // 500 MB limit
    public async Task<IActionResult> UploadManual(IFormFile? archiveFile, CancellationToken cancellationToken)
    {
        if (archiveFile == null || archiveFile.Length == 0)
        {
            TempData["ErrorMessage"] = "No archive file was selected for upload.";
            return RedirectToAction(nameof(Index));
        }

        var lockResult = await _lockLease.TryAcquireAsync("CompanyMasterSyncExclusiveLock", TimeSpan.Zero, cancellationToken);
        if (!lockResult.Succeeded)
        {
            TempData["ErrorMessage"] = $"Exclusive sync lock held by another process: {lockResult.FailureReason}";
            return RedirectToAction(nameof(Index));
        }

        string tempExtractDir = Path.Combine(Path.GetTempPath(), $"mca_upload_{Guid.NewGuid():N}");
        try
        {
            var job = await _deltaService.CreateJobAsync(CompanyMasterSyncTriggerType.ManualUpload, "ManualUpload", cancellationToken);
            await _deltaService.UpdateJobStatusAsync(job.JobId, CompanyMasterSyncJobStatus.Downloading, cancellationToken: cancellationToken);

            await using (var uploadStream = archiveFile.OpenReadStream())
            {
                var extractedFiles = await _archiveExtractor.ExtractSafelyAsync(uploadStream, tempExtractDir, cancellationToken);
                var csvFiles = extractedFiles.Where(f => f.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)).ToList();

                if (csvFiles.Count == 0)
                {
                    await _deltaService.UpdateJobStatusAsync(job.JobId, CompanyMasterSyncJobStatus.Failed, "Uploaded archive contained zero CSV files.", cancellationToken);
                    TempData["ErrorMessage"] = "Uploaded archive contained zero CSV files.";
                    return RedirectToAction(nameof(Index));
                }

                await _deltaService.IngestCsvFilesAsync(job.JobId, job.FencingToken, csvFiles, cancellationToken);
            }

            var validation = await _deltaService.ValidateStagingAsync(job.JobId, job.FencingToken, cancellationToken);
            if (!validation.IsValid)
            {
                TempData["ErrorMessage"] = $"Staging validation failed: {validation.ErrorMessage}";
                return RedirectToAction(nameof(Index));
            }

            var metrics = await _deltaService.PromoteStagedDeltaAsync(job.JobId, job.FencingToken, batchSize: 4000, cancellationToken);
            TempData["SuccessMessage"] = $"Manual upload sync completed successfully! Updated: {metrics.TotalUpdated:N0}, Inserted: {metrics.TotalInserted:N0}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing manual archive upload");
            TempData["ErrorMessage"] = $"Manual upload failed: {ex.Message}";
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, recursive: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean temp directory {Dir}", tempExtractDir);
            }

            await _lockLease.ReleaseAsync(CancellationToken.None);
        }

        return RedirectToAction(nameof(Index));
    }

    private async Task<CompanyMasterDashboardViewModel> BuildViewModelAsync(CancellationToken cancellationToken)
    {
        var model = new CompanyMasterDashboardViewModel
        {
            TotalCompanies = await _db.CompanyMasterRecords.CountAsync(r => r.RecordType == CompanyMasterRecordType.Company, cancellationToken),
            TotalLlps = await _db.CompanyMasterRecords.CountAsync(r => r.RecordType == CompanyMasterRecordType.Llp, cancellationToken),
            TotalForeign = await _db.CompanyMasterRecords.CountAsync(r => r.RecordType == CompanyMasterRecordType.Foreign, cancellationToken),
            ProxyNodes = _proxyPool.GetAllNodes(),
            Message = TempData["SuccessMessage"] as string,
            ErrorMessage = TempData["ErrorMessage"] as string
        };

        var latestCompleted = await _db.CompanyMasterSyncJobs
            .Where(j => j.Status == CompanyMasterSyncJobStatus.Completed)
            .OrderByDescending(j => j.JobId)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestCompleted != null)
        {
            model.LastSnapshotDate = latestCompleted.PublishedDate;
            model.LastSyncCompletedUtc = latestCompleted.CompletedUtc;
            model.LatestStatus = latestCompleted.Status.ToString();
        }

        model.RecentJobs = await _db.CompanyMasterSyncJobs
            .Include(j => j.Metrics)
            .OrderByDescending(j => j.JobId)
            .Take(20)
            .ToListAsync(cancellationToken);

        return model;
    }
}
