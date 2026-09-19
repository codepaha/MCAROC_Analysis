using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>
/// Internal audit log viewer strictly gated behind the feature-scoped "InternalReviewer" cookie scheme.
/// Enforces exact RequestId scoping and deterministic pagination.
/// </summary>
[Authorize(AuthenticationSchemes = "InternalReviewer")]
public class AuditLogController(AppDbContext db) : Controller
{
    [HttpGet("/internal/audit-logs/{requestId:long}")]
    public async Task<IActionResult> Index(long requestId, string? filter = null, int page = 1, int pageSize = 25, CancellationToken ct = default)
    {
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return NotFound();

        var query = db.AuditLogs.AsNoTracking().Where(a => a.RequestId == requestId);
        if (string.Equals(filter, "failures", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(a => a.Status == AuditStatus.Failure);
        }

        var totalCount = await query.CountAsync(ct);
        var effectivePageSize = Math.Clamp(pageSize, 1, 50);
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalCount / (double)effectivePageSize));
        var effectivePage = (page < 1 || page > totalPages) ? 1 : page;

        var items = await query
            .OrderByDescending(a => a.TimestampUtc)
            .ThenByDescending(a => a.AuditLogId)
            .Skip((effectivePage - 1) * effectivePageSize)
            .Take(effectivePageSize)
            .ToListAsync(ct);

        var model = new AuditLogPageViewModel
        {
            RequestId = requestId,
            RequestNumber = request.RequestNumber,
            CompanyName = request.CompanyName,
            Items = items,
            Page = effectivePage,
            PageSize = effectivePageSize,
            TotalCount = totalCount,
            TotalPages = totalPages,
            Filter = filter
        };

        return View("~/Views/AuditLog/Index.cshtml", model);
    }
}
