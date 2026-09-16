using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dossier;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Internal-only client settings — currently just the per-client dossier Litigation toggle
/// (<see cref="Client.IncludeLitigationInDossier"/>). Gated behind the same "InternalReviewer" cookie
/// scheme as <see cref="CalculationAuditController"/>: this can change what a client-facing PDF contains,
/// which is a materially different risk than the rest of this app's read-mostly, unauthenticated surface.</summary>
[Authorize(AuthenticationSchemes = "InternalReviewer")]
[Route("/Clients")]
public sealed class ClientsController(AppDbContext db, DossierCache dossierCache, IWebHostEnvironment env) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index() =>
        View(await db.Clients.OrderBy(c => c.ClientName).ToListAsync());

    [HttpGet("{id:long}/edit")]
    public async Task<IActionResult> Edit(long id)
    {
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        return View(new ClientEditViewModel
        {
            ClientId = client.ClientId, ClientName = client.ClientName, ClientCode = client.ClientCode,
            IncludeLitigationInDossier = client.IncludeLitigationInDossier
        });
    }

    [HttpPost("{id:long}/edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(long id, ClientEditViewModel model)
    {
        if (id != model.ClientId) return BadRequest();
        var client = await db.Clients.FindAsync(id);
        if (client is null) return NotFound();
        if (!ModelState.IsValid) return View(model);

        var litigationChanged = client.IncludeLitigationInDossier != model.IncludeLitigationInDossier;
        client.IncludeLitigationInDossier = model.IncludeLitigationInDossier;
        client.UpdatedDate = DateTime.UtcNow;
        await db.SaveChangesAsync();

        // Neither the in-memory nor the on-disk dossier cache is keyed on this setting, so a stale PDF (or
        // cached DossierModel) would otherwise keep serving the old value for up to 20 minutes (memory) or
        // indefinitely (disk — that cache has no expiration at all). Bust every affected request's cache now
        // so the very next view/download reflects the change.
        if (litigationChanged)
        {
            var requestIds = await db.Requests.Where(r => r.ClientId == id).Select(r => r.RequestId).ToListAsync();
            foreach (var requestId in requestIds)
                await dossierCache.InvalidateForRequestAsync(requestId, env.ContentRootPath);
        }

        TempData["Message"] = $"Saved. {client.ClientName}'s dossier settings updated.";
        return RedirectToAction(nameof(Index));
    }
}
