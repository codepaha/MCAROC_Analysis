using MCAROC_Analysis.Data;
using MCAROC_Analysis.Services.CalculationAssurance;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Serves the client "Due Diligence Dossier" PDF. The assembled model comes from
/// <see cref="DossierCache"/> (one build per data change); the rendered PDF is file-cached per
/// (request, ingestion run, analysis run, variant) and written atomically, so a concurrent
/// double-download never serves a half-written file.</summary>
public class DossierController(
    AppDbContext db, DossierCache cache, DossierPdfRenderer renderer, CalculationArtifactGateService gate,
    IWebHostEnvironment env) : Controller
{
    [HttpGet("/Requests/{id:long}/dossier")]
    public async Task<IActionResult> Download(long id, [FromQuery] string? variant, CancellationToken ct)
    {
        var flavour = variant?.Trim().ToLowerInvariant() switch
        {
            "full" => DossierVariant.FullSource,
            "source" or "sourcerecord" or "source-record" => DossierVariant.SourceRecord,
            _ => DossierVariant.Executive
        };

        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == id, ct);
        if (request is null) return NotFound();

        var model = await cache.GetAsync(id, ct);
        if (model is null)
            return StatusCode(StatusCodes.Status409Conflict,
                "The dossier is not ready — it needs a completed ingestion and a completed analysis of that " +
                "same data. Re-run the analysis if the source documents were re-ingested.");

        // #164 calculation-assurance delivery gate — deliberately before any file-cache read below, so a
        // PDF rendered and cached to disk before a hold existed is still blocked, not served stale from
        // cache. A held artifact returns the exact same NotFound() as a genuinely-missing request just
        // above — this never distinguishes "held" from "doesn't exist" to the caller.
        if (await gate.IsHeldAsync(id, model.IngestionRunId, model.AnalysisRunId, flavour, ct))
            return NotFound();

        var dir = Path.Combine(env.ContentRootPath, "App_Data", "Dossiers", id.ToString());
        Directory.CreateDirectory(dir);
        var name = $"{flavour.ToString().ToLowerInvariant()}-{model.IngestionRunId}-{model.AnalysisRunId?.ToString() ?? "none"}.pdf";
        var path = Path.Combine(dir, name);

        if (!System.IO.File.Exists(path))
        {
            var bytes = renderer.Render(model, flavour);
            var tmp = path + $".{Guid.NewGuid():N}.tmp";
            await System.IO.File.WriteAllBytesAsync(tmp, bytes, ct);
            try { System.IO.File.Move(tmp, path, overwrite: true); }
            catch (IOException) { System.IO.File.Delete(tmp); } // another request won the race — its file stands
        }

        var label = flavour switch
        {
            DossierVariant.FullSource => "Full source",
            DossierVariant.SourceRecord => "Source records",
            _ => "Executive"
        };
        var download = $"Due Diligence Dossier - {request.RequestNumber} - {label}.pdf";
        return PhysicalFile(path, "application/pdf", download);
    }
}
