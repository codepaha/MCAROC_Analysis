using MCAROC_Analysis.Services.CalculationAssurance;
using Microsoft.AspNetCore.Hosting;

namespace MCAROC_Analysis.Services.Dossier;

/// <param name="Rendered">The PDF is on disk at <see cref="Path"/> — either just written by this call, or
/// already there from an earlier one (either way, safe for the caller to serve).</param>
/// <param name="NotReady">No completed-ingestion + completed-analysis model exists yet — nothing to render.</param>
/// <param name="Held">A calculation-assurance hold blocks this artifact (plan §4.3) — never rendered while held,
/// even if an older, now-stale file happens to sit at the same cache path from before the hold existed.</param>
public sealed record DossierRenderResult(bool Rendered, bool NotReady, bool Held, string? Path);

/// <summary>Render + atomic write + cache path, shared by <c>DossierController.Download</c> (a reviewer's
/// request) and the pipeline coordinator's automatic pre-render (plan §4.3) — one place decides the on-disk
/// key and the hold check, so the two callers can never disagree about what "ready" means. Idempotent and
/// cheap to call repeatedly: an existing file is a single <see cref="File.Exists"/> check away.</summary>
public sealed class DossierArtifactService(DossierCache cache, DossierPdfRenderer renderer, CalculationArtifactGateService gate, IWebHostEnvironment env)
{
    public async Task<DossierRenderResult> EnsureRenderedAsync(long requestId, DossierVariant flavour, CancellationToken ct)
    {
        var model = await cache.GetAsync(requestId, ct);
        if (model is null) return new DossierRenderResult(false, true, false, null);

        // Deliberately before any file-cache read below — see DossierController's own remark: a PDF rendered
        // and cached to disk before a hold existed must still be treated as blocked, not served stale.
        if (await gate.IsHeldAsync(requestId, model.IngestionRunId, model.AnalysisRunId, flavour, ct))
            return new DossierRenderResult(false, false, true, null);

        var dir = Path.Combine(env.ContentRootPath, "App_Data", "Dossiers", requestId.ToString());
        Directory.CreateDirectory(dir);
        // The litigation evidence version is part of the name: the PDF carries charge-to-case links built from it, and
        // this file cache never expires. A refresh, or order text extracted later under the same snapshot, must
        // produce a different file rather than let File.Exists serve a PDF that is missing those links.
        var litigationPart = model.ChargeLinks?.Version is { } litigationVersion ? $"-l{litigationVersion}" : "";
        var name = $"{flavour.ToString().ToLowerInvariant()}-{model.IngestionRunId}-{model.AnalysisRunId?.ToString() ?? "none"}{litigationPart}.pdf";
        var path = Path.Combine(dir, name);

        if (!File.Exists(path))
        {
            var bytes = renderer.Render(model, flavour);
            var tmp = path + $".{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct);
            try { File.Move(tmp, path, overwrite: true); }
            catch (IOException) { File.Delete(tmp); } // another caller won the race — its file stands
        }

        return new DossierRenderResult(true, false, false, path);
    }
}
