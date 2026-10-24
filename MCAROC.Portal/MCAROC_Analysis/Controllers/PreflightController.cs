using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.AnalystAccess;
using MCAROC_Analysis.Services.Audit;
using MCAROC_Analysis.Services.Preflight;
using Microsoft.AspNetCore.Mvc;

namespace MCAROC_Analysis.Controllers;

/// <summary>The pre-flight check (#514), run at the start of every E2E round. Full access only: it shows integration errors and configuration.
/// <c>?format=json</c> returns the same report for scripts. Each run is kept in the audit log, so a request's run record can show the check made
/// before the request was created.</summary>
public class PreflightController(PreflightService preflight, IAuditLogService audit) : Controller
{
    [HttpGet("/Preflight")]
    public async Task<IActionResult> Index(string? format, CancellationToken ct)
    {
        if (!PortalAccess.HasFullAccess(User)) return NotFound();
        var report = await preflight.RunAsync(ct);
        await audit.TryLogAsync(new AuditEvent<PreflightRunPayload>(
            AuditActionType.PreflightRun, AuditEventKind.DomainLifecycle,
            report.Overall switch { PreflightOutcome.Fail => AuditStatus.Failure, PreflightOutcome.Warn => AuditStatus.Warning, _ => AuditStatus.Success },
            ActorType.AuthenticatedReviewer, User.Identity?.Name ?? "portal", CorrelationContext.GetOrCreate(HttpContext),
            Payload: PreflightRunPayload.From(report)), ct);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            return Json(new
            {
                checkedUtc = report.CheckedUtc,
                overall = report.Overall.ToString(),
                checks = report.Checks.Select(c => new { c.Key, c.Name, outcome = c.Outcome.ToString(), c.Detail, c.Fix, c.ElapsedMs })
            });
        return View(report);
    }
}

/// <summary>A pre-flight run as kept in the audit log: every check's outcome, and the detail of those that did not pass (trimmed so the whole run
/// fits the log's payload cap).</summary>
public sealed record PreflightRunPayload(string Overall, DateTime CheckedUtc, IReadOnlyList<PreflightRunPayload.Item> Checks)
{
    public sealed record Item(string Key, string Outcome, string? Detail);

    public static PreflightRunPayload From(PreflightReport report) => new(report.Overall.ToString(), report.CheckedUtc,
        report.Checks.Select(c => new Item(c.Key, c.Outcome.ToString(),
            c.Outcome == PreflightOutcome.Pass ? null : c.Detail.Length > 80 ? c.Detail[..80] + "…" : c.Detail)).ToList());
}
