using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AutoFetch;
using MCAROC_Analysis.Services.LitigationData;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>#243/LIT-03's one product-facing surface ahead of the full litigation UI (#246/#247): a
/// request-scoped bulk ZIP of every currently-retained order PDF. Internal-reviewer only, same auth scheme as
/// every other request-scoped document download in <c>RequestsController</c> — litigation order PDFs are not
/// part of the public/client-facing report surface (see epic #239's report contract: "No source vendor order
/// URL will be published in the report").</summary>
public class LitigationController(AppDbContext db, IWebHostEnvironment env, LitigationAiAnalysisOrchestrator analysis,
    ILogger<LitigationController>? logger = null) : Controller
{
    /// <summary>Starts an evidence-only LIT-05 run, or returns the already-active run. The response is
    /// deliberately lifecycle metadata; completed case/portfolio output is read from the persisted endpoint.
    /// This keeps the paid call asynchronous and prevents a browser refresh from issuing a second request.</summary>
    [HttpPost("/Requests/{id:long}/Litigation/Analysis")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    public async Task<IActionResult> StartAnalysis(long id, CancellationToken ct)
    {
        if (!await db.Requests.AnyAsync(r => r.RequestId == id, ct)) return NotFound();
        var run = await analysis.CreateOrJoinAsync(id, ct);
        return Accepted(new { run.LitigationAiAnalysisRunId, run.RunNumber, status = run.Status.ToString(), run.CreatedUtc });
    }

    /// <summary>Returns a render-ready projection made entirely from persisted auditable records. Pending and
    /// failed states remain visible rather than being replaced by a made-up conclusion.</summary>
    [HttpGet("/Requests/{id:long}/Litigation/Analysis")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
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

    [HttpGet("/Requests/{id:long}/Litigation/OrdersZip")]
    [Authorize(AuthenticationSchemes = "InternalReviewer")]
    public async Task<IActionResult> DownloadOrdersZip(long id, CancellationToken ct)
    {
        var request = await db.Requests.Where(r => r.RequestId == id)
            .Select(r => new { r.RequestId, r.CompanyName }).FirstOrDefaultAsync(ct);
        if (request is null) return NotFound();

        // Only Downloaded — "currently retained" excludes Pending/InProgress (not fetched yet) and
        // Failed/Expired (no file to include) by construction, never by a separate flag to keep in sync.
        var documents = await db.LitigationOrderDocuments
            .Where(d => d.Order!.Case!.RequestId == id && d.Status == LitigationOrderDocumentStatus.Downloaded)
            .Select(d => new
            {
                d.LitigationOrderDocumentId, d.StoragePath, d.Order!.OrderType, d.Order.OrderDate,
                CaseLabel = d.Order.Case!.CaseNumber ?? d.Order.Case.Cnr
            })
            .ToListAsync(ct);

        var expectedDir = Path.GetFullPath(
            Path.Combine(env.ContentRootPath, "App_Data", "Requests", id.ToString(), "litigation-orders")) + Path.DirectorySeparatorChar;

        var entries = new List<LitigationOrderArchiveEntry>();
        foreach (var d in documents)
        {
            if (string.IsNullOrWhiteSpace(d.StoragePath)) continue;

            var fullPath = Path.GetFullPath(d.StoragePath);
            if (!fullPath.StartsWith(expectedDir, StringComparison.OrdinalIgnoreCase))
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
            return NotFound("No currently retained litigation order PDFs are available for this request.");

        var zipDir = Path.Combine(env.ContentRootPath, "App_Data", "Requests", id.ToString(), "litigation-orders", "_zip");
        Directory.CreateDirectory(zipDir);
        var zipPath = Path.Combine(zipDir, $"{Guid.NewGuid():N}.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries, ct);
        if (written == 0)
        {
            TryDelete(zipPath);
            return NotFound("No currently retained litigation order PDFs are available for this request.");
        }

        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var fileName = $"LitigationOrders_{AutoFetchArchiveBuilder.CompanyToken(request.CompanyName ?? "Company")}_{id}.zip";
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

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort cleanup — a leftover temp ZIP is harmless, never worth failing the request over */ }
    }
}
