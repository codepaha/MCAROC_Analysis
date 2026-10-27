using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using MCAROC_Analysis.Services.Storage;

namespace MCAROC_Analysis.Controllers;

/// <summary>Request-scoped litigation endpoints: BPR search triggering, status polling, safe order PDF
/// downloads, bulk order ZIP delivery, and evidence-grounded AI analysis.</summary>
public class LitigationController(
    AppDbContext db,
    IWebHostEnvironment env,
    IOptions<BprLitigationOptions> bprOptions,
    LitigationStartService starter,
    ILogger<LitigationController>? logger = null,
    IStoragePathResolver? pathResolver = null) : Controller
{
    private readonly BprLitigationOptions _opts = bprOptions.Value;
    private readonly IStoragePathResolver _pathResolver = pathResolver ?? StoragePathHelper.Default;

    /// <summary>Starts an evidence-only LIT-05 run, or returns the already-active run. The response is
    /// deliberately lifecycle metadata; completed case/portfolio output is read from the persisted endpoint.
    /// This keeps the paid call asynchronous and prevents a browser refresh from issuing a second request.</summary>
    [HttpPost("/Requests/{id:long}/Litigation/Analysis")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAnalysis(long id, CancellationToken ct)
    {
        var clientId = await db.Requests.Where(r => r.RequestId == id).Select(r => (long?)r.ClientId).FirstOrDefaultAsync(ct);
        if (clientId is null) return NotFound();
        var started = await starter.StartAnalysisAsync(id, clientId, PaidCallTrigger.Manual, ct);
        if (!started.Started) return Conflict(new { error = started.Message });
        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().SingleAsync(r => r.LitigationAiAnalysisRunId == started.ReferenceId, ct);
        return Accepted(new { run.LitigationAiAnalysisRunId, run.RunNumber, status = run.Status.ToString(), run.CreatedUtc });
    }

    /// <summary>Returns a render-ready projection made entirely from persisted auditable records. Pending and
    /// failed states remain visible rather than being replaced by a made-up conclusion.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Analysis")]
    public async Task<IActionResult> GetAnalysis(long id, CancellationToken ct)
    {
        if (!await db.Requests.AnyAsync(r => r.RequestId == id, ct)) return NotFound();
        var run = await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == id)
            .OrderByDescending(x => x.RunNumber).Select(x => new
            {
                x.LitigationAiAnalysisRunId, x.RunNumber, Status = x.Status.ToString(), x.AttemptCount,
                x.CreatedUtc, x.CompletedUtc, x.FailureReason,
                Cases = x.CaseAnalyses.OrderBy(c => c.LitigationCaseId).Select(c => new
                { c.LitigationCaseId, Status = c.Status.ToString(), c.AnalysisJson, c.FailureReason, c.CompletedUtc }),
                Portfolio = x.PortfolioAnalysis == null ? null : new
                { Status = x.PortfolioAnalysis.Status.ToString(), x.PortfolioAnalysis.AnalysisJson, x.PortfolioAnalysis.FailureReason, x.PortfolioAnalysis.CompletedUtc }
            }).FirstOrDefaultAsync(ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>Lifecycle-only polling: never loads case analyses or extracted order evidence.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/AnalysisProgress")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetAnalysisProgress(long id, CancellationToken ct)
    {
        var run = await db.LitigationAiAnalysisRuns.AsNoTracking().Where(x => x.RequestId == id)
            .OrderByDescending(x => x.RunNumber).Select(x => new
            { x.LitigationAiAnalysisRunId, x.RunNumber, status = x.Status.ToString(), x.CompletedUtc })
            .FirstOrDefaultAsync(ct);
        return run is null ? NotFound() : Ok(run);
    }

    /// <summary>Adds name variations for the request's company (one per line) and re-runs its search with them (see <see cref="NameVariationService"/>).
    /// A client account is held to the refresh limits like any other re-run; the internal full-access user is not.</summary>
    [HttpPost("/Requests/{id:long}/Litigation/NameVariations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddNameVariations(long id, [FromForm] string? names, CancellationToken ct)
    {
        if (!await db.Requests.AnyAsync(r => r.RequestId == id, ct)) return NotFound();
        var lines = (names ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            TempData["LitigationSearchError"] = "Enter at least one name variation, one per line.";
            return Redirect($"/Requests/{id}#tab-litigation");
        }
        if (!Services.AnalystAccess.PortalAccess.HasFullAccess(User)
            && await LitigationRefreshPolicy.GetAsync(db, id, _opts, DateTime.UtcNow, ct) is { Allowed: false } refresh)
        {
            TempData["LitigationSearchError"] = refresh.Reason;
            return Redirect($"/Requests/{id}#tab-litigation");
        }

        var service = HttpContext.RequestServices.GetRequiredService<NameVariationService>();
        var outcome = await service.AddAsync(id, lines, User.Identity?.Name ?? "unknown", ct);
        var summary = string.Join(" ", outcome.Results.Select(r => r.Status == NameVariationAddStatus.Added
            ? $"Added \"{r.Name}\".{(r.Warning is null ? "" : " " + r.Warning)}"
            : $"\"{r.Name}\" not added: {r.Message}"));
        var search = outcome.Search switch
        {
            null => "",
            { Started: true } => " The search has been started with the new names.",
            { Denial: AdmissionDenial.InFlight } => " A search is already running; the new names are searched as soon as it finishes.",
            { } s => $" The search could not be started: {s.Message}"
        };
        if (outcome.Problem is not null) TempData["LitigationSearchError"] = outcome.Problem;
        else if (outcome.AddedCount > 0) TempData["LitigationSearchOk"] = summary + search;
        else TempData["LitigationSearchError"] = summary;
        return Redirect($"/Requests/{id}#tab-litigation");
    }

    /// <summary>Removes a name variation: it is no longer searched, and the cases it alone made the company's are assessed again without it.</summary>
    [HttpPost("/Requests/{id:long}/Litigation/NameVariations/{variationId:long}/Remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveNameVariation(long id, long variationId, CancellationToken ct)
    {
        var service = HttpContext.RequestServices.GetRequiredService<NameVariationService>();
        if (await service.RemoveAsync(id, variationId, User.Identity?.Name ?? "unknown", ct))
            TempData["LitigationSearchOk"] = "Name variation removed. It is no longer searched; the cases it alone made the company's are being assessed again.";
        else
            TempData["LitigationSearchError"] = "That name variation was not found or was already removed.";
        return Redirect($"/Requests/{id}#tab-litigation");
    }

    /// <summary>Starts or reruns a BPR litigation search for the request. Evaluates fail-closed eligibility
    /// before queuing the job. The first search is free; once a report exists another search is a client refresh,
    /// which is chargeable and must carry <paramref name="confirmChargeableRefresh"/> (set by the tab's confirm
    /// pop-up) — a refresh posted without it is refused rather than run.</summary>
    [HttpPost("/Requests/{id:long}/Litigation/Search")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartSearch(long id, CancellationToken ct, [FromForm] bool confirmChargeableRefresh = false)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id, ct);
        if (request is null) return NotFound();

        var (plan, problem) = await LitigationStartService.PlanSearchAsync(db, request, _opts, ct,
            HttpContext?.RequestServices?.GetService<LitigationKeywordSuggester>());
        if (plan is null)
            return BadRequest(problem);

        // The first search, or a retry of one that never produced a report, is free. Once a report exists,
        // another search is a refresh: capped in number and spaced apart.
        var hasReport = await db.LitigationReportSnapshots.AsNoTracking()
            .AnyAsync(s => s.SearchJob!.RequestId == id && s.Status == LitigationReportSnapshotStatus.Completed, ct);
        if (hasReport)
        {
            if (!confirmChargeableRefresh)
            {
                const string unconfirmed = "Refreshing litigation data is chargeable. Confirm the refresh to continue.";
                if (Request.Headers.Accept.ToString().Contains("application/json"))
                    return Conflict(new { error = unconfirmed });
                TempData["LitigationSearchError"] = unconfirmed;
                return Redirect($"/Requests/{id}#tab-litigation");
            }

            var refresh = await LitigationRefreshPolicy.GetAsync(db, id, _opts, DateTime.UtcNow, ct,
                unlimited: Services.AnalystAccess.PortalAccess.HasFullAccess(User));
            if (!refresh.Allowed)
            {
                if (Request.Headers.Accept.ToString().Contains("application/json"))
                    return Conflict(new { error = refresh.Reason });
                TempData["LitigationSearchError"] = refresh.Reason;
                return Redirect($"/Requests/{id}#tab-litigation");
            }
        }

        try
        {
            var started = await starter.StartSearchAsync(request, plan.Keywords, plan.EntityType, plan.ApplicationCustomerId, PaidCallTrigger.Manual, ct);
            if (!started.Started)
                throw new InvalidOperationException(started.Message);
            var job = await db.LitigationSearchJobs.AsNoTracking().SingleAsync(j => j.LitigationSearchJobId == started.ReferenceId, ct);

            if (Request.Headers.Accept.ToString().Contains("application/json"))
                return Accepted(new { job.LitigationSearchJobId, status = job.Status.ToString(), job.CreatedUtc, reused = started.Message });

            // #291: a reuse completes synchronously (nothing was queued) and started.Message carries the
            // disclosure text — a fuller on-tab provenance banner is a separate, later piece of work.
            TempData["LitigationSearchOk"] = started.Message ?? "Litigation search has been queued.";
            return Redirect($"/Requests/{id}#tab-litigation");
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex, "Failed to start litigation search for Request {RequestId}: active search already in progress", id);
            if (Request.Headers.Accept.ToString().Contains("application/json"))
                return Conflict(new { error = ex.Message });

            TempData["LitigationSearchError"] = ex.Message;
            return Redirect($"/Requests/{id}#tab-litigation");
        }
    }

    /// <summary>Returns the current search job and snapshot import lifecycle state for live client polling.
    /// Responds with no-store to prevent proxy caching.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Status")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> GetSearchStatus(long id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store, private";

        var job = await db.LitigationSearchJobs.AsNoTracking().FirstOrDefaultAsync(j => j.RequestId == id, ct);
        if (job is null) return NotFound();

        LitigationReportSnapshot? currentSnapshot = null;
        if (!string.IsNullOrWhiteSpace(job.RawResponseHash))
        {
            currentSnapshot = await db.LitigationReportSnapshots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.LitigationSearchJobId == job.LitigationSearchJobId && s.ReportHash == job.RawResponseHash, ct);
        }

        string importState;
        bool isTerminal;
        bool isFullyIndexed;

        if (string.IsNullOrWhiteSpace(job.RawResponseHash))
        {
            importState = "NotCreated";
            isTerminal = job.IsTerminal;
            isFullyIndexed = false;
        }
        else if (currentSnapshot is null)
        {
            if (job.Status == LitigationSearchJobStatus.Completed)
            {
                importState = "SnapshotMissing";
                isTerminal = true;
                isFullyIndexed = false;
            }
            else
            {
                importState = "NotCreated";
                isTerminal = job.IsTerminal;
                isFullyIndexed = false;
            }
        }
        else
        {
            importState = currentSnapshot.Status.ToString();
            isTerminal = job.IsTerminal && currentSnapshot.IsTerminal;
            isFullyIndexed = job.Status == LitigationSearchJobStatus.Completed && currentSnapshot.Status == LitigationReportSnapshotStatus.Completed;
        }

        return Ok(new
        {
            searchJobStatus = job.Status.ToString(),
            progressPercent = job.ProgressPercent,
            statusMessage = job.StatusMessage,
            failureReason = job.FailureReason ?? currentSnapshot?.FailureReason,
            importState,
            casesPersisted = currentSnapshot?.CasesPersistedCount ?? 0,
            isFullyIndexed,
            isTerminal
        });
    }

    /// <summary>Safely streams one downloaded litigation order PDF. Validates internal reviewer authentication,
    /// canonical directory path, and physical file existence.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Orders/{documentId:long}/download")]
    public async Task<IActionResult> DownloadOrderDocument(long id, long documentId, CancellationToken ct)
    {
        var doc = await db.LitigationOrderDocuments
            .Where(d => d.LitigationOrderDocumentId == documentId && d.Order!.Case!.RequestId == id)
            .Select(d => new
            {
                d.LitigationOrderDocumentId,
                d.Status,
                d.StoragePath,
                d.Order!.OrderDate,
                d.Order.OrderType,
                CaseLabel = d.Order.Case!.CaseNumber ?? d.Order.Case.Cnr
            })
            .FirstOrDefaultAsync(ct);

        if (doc is null || doc.Status != LitigationOrderDocumentStatus.Downloaded || string.IsNullOrWhiteSpace(doc.StoragePath))
            return NotFound("Order document not found or not downloaded.");

        var expectedDir = _pathResolver.GetLitigationOrdersDir(id);

        var fullPath = _pathResolver.ResolveFullPath(doc.StoragePath);
        if (!_pathResolver.IsContained(fullPath, expectedDir))
        {
            logger?.LogError(
                "Path traversal detected on litigation order document {Id} for Request {RequestId}",
                documentId, id);
            return BadRequest("Invalid document path.");
        }

        if (!System.IO.File.Exists(fullPath))
            return NotFound("Order PDF file is not available on disk.");

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        // An order the source served as an HTML page is stored as its plain text: served as text, never as PDF or HTML.
        var isText = string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase);
        var safeCase = string.IsNullOrWhiteSpace(doc.CaseLabel) ? "Case" : AutoFetchArchiveBuilder.CompanyToken(doc.CaseLabel);
        var fileName = $"Order_{safeCase}_{doc.OrderDate ?? "undated"}_{documentId}{(isText ? ".txt" : ".pdf")}";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        return PhysicalFile(fullPath, isText ? "text/plain; charset=utf-8" : "application/pdf");
    }

    [HttpGet("/Requests/{id:long}/Litigation/OrdersZip")]
    public Task<IActionResult> DownloadOrdersZip(long id, CancellationToken ct) => ServeOrdersZipAsync(id, null, ct);

    /// <summary>Every retained order PDF of one case, in one ZIP (the case page's "Download all orders").</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Cases/{caseId:long}/OrdersZip")]
    public Task<IActionResult> DownloadCaseOrdersZip(long id, long caseId, CancellationToken ct) => ServeOrdersZipAsync(id, caseId, ct);

    private async Task<IActionResult> ServeOrdersZipAsync(long id, long? caseId, CancellationToken ct)
    {
        var request = await db.Requests.Where(r => r.RequestId == id)
            .Select(r => new { r.RequestId, r.CompanyName }).FirstOrDefaultAsync(ct);
        if (request is null) return NotFound();

        // Only Downloaded — "currently retained" excludes Pending/InProgress (not fetched yet) and
        // Failed/Expired (no file to include) by construction, never by a separate flag to keep in sync.
        var documents = await db.LitigationOrderDocuments
            .Where(d => d.Order!.Case!.RequestId == id && d.Status == LitigationOrderDocumentStatus.Downloaded
                && (caseId == null || d.Order.LitigationCaseId == caseId))
            .Select(d => new
            {
                d.LitigationOrderDocumentId, d.StoragePath, d.Order!.OrderType, d.Order.OrderDate,
                CaseLabel = d.Order.Case!.CaseNumber ?? d.Order.Case.Cnr
            })
            .ToListAsync(ct);

        var expectedDir = _pathResolver.GetLitigationOrdersDir(id);

        var entries = new List<LitigationOrderArchiveEntry>();
        foreach (var d in documents)
        {
            if (string.IsNullOrWhiteSpace(d.StoragePath)) continue;

            var fullPath = _pathResolver.ResolveFullPath(d.StoragePath);
            if (!_pathResolver.IsContained(fullPath, expectedDir))
            {
                logger?.LogError(
                    "Path traversal detected on litigation order document {Id} for Request {RequestId}",
                    d.LitigationOrderDocumentId, id);
                continue;
            }

            var caseLabel = d.CaseLabel ?? "Case";
            var entryLabel = $"{d.OrderType ?? "Order"}_{d.OrderDate ?? "unknown-date"}_{d.LitigationOrderDocumentId}";
            entries.Add(new LitigationOrderArchiveEntry(caseLabel, entryLabel, fullPath));
        }

        if (entries.Count == 0)
            return NotFound(caseId is null ? "No currently retained litigation order PDFs are available for this request." : "No retained order PDFs are available for this case.");

        var zipDir = Path.Combine(_pathResolver.GetLitigationOrdersDir(id), "_zip");
        Directory.CreateDirectory(zipDir);
        var zipPath = Path.Combine(zipDir, $"{Guid.NewGuid():N}.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries, ct);
        if (written == 0)
        {
            TryDelete(zipPath);
            return NotFound(caseId is null ? "No currently retained litigation order PDFs are available for this request." : "No retained order PDFs are available for this case.");
        }

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var fileName = caseId is null
            ? $"LitigationOrders_{AutoFetchArchiveBuilder.CompanyToken(request.CompanyName ?? "Company")}_{id}.zip"
            : $"CaseOrders_{AutoFetchArchiveBuilder.CompanyToken(documents[0].CaseLabel ?? "Case")}_{caseId}.zip";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        // The temp ZIP is built fresh per download (order-document retention/expiry is dynamic — never
        // served stale) and removed once the response has finished streaming it, not before.
        Response.OnCompleted(() =>
        {
            TryDelete(zipPath);
            return Task.CompletedTask;
        });
        return PhysicalFile(zipPath, "application/zip");
    }

    /// <summary>Generates and downloads the standalone litigation due diligence PDF report.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Report/pdf")]
    public async Task<IActionResult> DownloadPdfReport(long id, [FromServices] LitigationReportAssembler assembler, CancellationToken ct, [FromQuery] bool withAnalysis = false)
    {
        if (withAnalysis && !await HasCompletedAnalysisAsync(id, ct))
            return NotFound("No completed AI analysis is available for this request yet.");
        var report = await assembler.AssembleAsync(id, ct, includeAnalysis: withAnalysis);
        if (report is null)
            return NotFound("No completed litigation snapshot is available for this request.");

        var pdfBytes = LitigationReportArtifacts.RenderPdf(report, LitigationReportArtifacts.TryLoadLogo(env.WebRootPath));
        var safeCompany = AutoFetchArchiveBuilder.CompanyToken(report.CompanyName);
        var fileName = $"LitigationReport_{(withAnalysis ? "WithAnalysis" : "PreAnalysis")}_{safeCompany}_{id}.pdf";

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        return File(pdfBytes, "application/pdf");
    }

    /// <summary>Generates and downloads the standalone litigation case register CSV export.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Report/csv")]
    public async Task<IActionResult> DownloadCsvReport(long id, [FromServices] LitigationReportAssembler assembler, CancellationToken ct, [FromQuery] bool withAnalysis = false)
    {
        if (withAnalysis && !await HasCompletedAnalysisAsync(id, ct))
            return NotFound("No completed AI analysis is available for this request yet.");
        var report = await assembler.AssembleAsync(id, ct, includeAnalysis: withAnalysis);
        if (report is null)
            return NotFound("No completed litigation snapshot is available for this request.");

        var csvBytes = LitigationReportArtifacts.RenderCsv(report);
        var safeCompany = AutoFetchArchiveBuilder.CompanyToken(report.CompanyName);
        var fileName = $"LitigationReport_{(withAnalysis ? "WithAnalysis" : "PreAnalysis")}_{safeCompany}_{id}.csv";

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var contentDisposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        contentDisposition.SetHttpFileName(fileName);
        Response.Headers.ContentDisposition = contentDisposition.ToString();

        return File(csvBytes, "text/csv; charset=utf-8");
    }

    private Task<bool> HasCompletedAnalysisAsync(long id, CancellationToken ct) =>
        db.LitigationAiAnalysisRuns.AsNoTracking().AnyAsync(r => r.RequestId == id
            && (r.Status == LitigationAiAnalysisRunStatus.Completed || r.Status == LitigationAiAnalysisRunStatus.CompletedWithErrors), ct);

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort cleanup — a leftover temp ZIP is harmless, never worth failing the request over */ }
    }
}
