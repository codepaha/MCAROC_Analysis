using MCAROC_Analysis.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>Internal client list. It used to hold the per-client "Litigation in the dossier" switch; the dossier no longer carries
/// litigation for anyone (it has its own standalone report), so the switch and its edit page are gone and this is read-only.
/// <see cref="Data.Entities.Client.IncludeLitigationInDossier"/> stays in the table, unused, so no migration is needed.</summary>
[Route("/Clients")]
public sealed class ClientsController(AppDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index() =>
        View(await db.Clients.OrderBy(c => c.ClientName).ToListAsync());
}
