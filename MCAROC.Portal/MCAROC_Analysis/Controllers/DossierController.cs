using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Serves the client "Due Diligence Dossier" PDF. Render + atomic write + cache path is shared with
/// the pipeline coordinator's automatic pre-render via <see cref="DossierArtifactService"/> (plan §4.3) — this
/// controller only turns that shared result into an HTTP response.</summary>
public class DossierController(AppDbContext db, DossierArtifactService artifacts, IAuditLogService? audit = null) : Controller
{
    [HttpGet("/Requests/{id:long}/dossier")]
    public async Task<IActionResult> Download(long id, [FromQuery] string? variant, CancellationToken ct)
    {
        // The FullSource/SourceRecord variants were removed (their raw worksheet dump is superseded by
        // the portal's per-domain tabs) — any variant query value now resolves to the one remaining flavour,
        // so an old bookmarked link still downloads a PDF instead of erroring.
        var flavour = DossierVariant.Executive;

        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id, ct);
        if (request is null) return NotFound();

        var result = await artifacts.EnsureRenderedAsync(id, flavour, ct);
        if (result.NotReady)
            return StatusCode(StatusCodes.Status409Conflict,
                "The dossier is not ready — it needs a completed ingestion and a completed analysis of that " +
                "same data. Re-run the analysis if the source documents were re-ingested.");
        // A held artifact returns the exact same NotFound() as a genuinely-missing request just above — this
        // never distinguishes "held" from "doesn't exist" to the caller (#164).
        if (result.Held || result.Path is null) return NotFound();

        var download = $"Due Diligence Dossier - {request.RequestNumber} - Executive.pdf";
        // #514: the request's run record shows that a dossier was downloaded, and which; nothing else records it.
        if (audit is not null)
            await audit.TryLogAsync(new AuditEvent<DossierDownloadPayload>(
                AuditActionType.DossierDownloaded, AuditEventKind.DomainLifecycle, AuditStatus.Success,
                ActorType.AuthenticatedReviewer, User.Identity?.Name ?? "portal", CorrelationContext.GetOrCreate(HttpContext),
                RequestId: id, EntityType: "Dossier", Payload: DossierDownloadPayload.Of(flavour.ToString(), result.Path)), ct);
        return PhysicalFile(result.Path, "application/pdf", download);
    }
}

/// <summary>A dossier download as kept in the audit log: the flavour, the file's size and its page count (null if the file could not be read).</summary>
public sealed record DossierDownloadPayload(string Variant, long Bytes, int? Pages)
{
    public static DossierDownloadPayload Of(string variant, string path)
    {
        var bytes = new FileInfo(path).Length;
        int? pages;
        try { using var pdf = UglyToad.PdfPig.PdfDocument.Open(path); pages = pdf.NumberOfPages; }
        catch (Exception) { pages = null; }
        return new DossierDownloadPayload(variant, bytes, pages);
    }
}
