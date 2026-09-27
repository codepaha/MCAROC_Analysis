using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Serves the client "Due Diligence Dossier" PDF. Render + atomic write + cache path is shared with
/// the pipeline coordinator's automatic pre-render via <see cref="DossierArtifactService"/> (plan §4.3) — this
/// controller only turns that shared result into an HTTP response.</summary>
public class DossierController(AppDbContext db, DossierArtifactService artifacts) : Controller
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
        return PhysicalFile(result.Path, "application/pdf", download);
    }
}
