using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.CalculationAssurance;
using MCAROC_Analysis.Services.CalculationAssurance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Controllers;

/// <summary>The #164 internal calculation-audit review panel — ledger, deterministic check results, and
/// the discrepancy workflow (triage/confirm/reject/accept-exception/mark-fixed-pending-reaudit/resolve).
/// Gated behind the feature-scoped "InternalReviewer" cookie scheme (InternalAuthController) — named
/// explicitly here, not the application default, so no other endpoint in this app is affected by this
/// scheme merely being registered. Both viewing and every mutating action require sign-in: a discrepancy
/// decision here can release a delivery hold, which is a materially different risk than the rest of this
/// app's read-mostly, unauthenticated surface.</summary>
[Authorize(AuthenticationSchemes = "InternalReviewer")]
public class CalculationAuditController(AppDbContext db, CalculationDiscrepancyWorkflowService workflow) : Controller
{
    [HttpGet("/internal/calc-audit/{requestId:long}")]
    public async Task<IActionResult> Index(long requestId, long? snapshotId, CancellationToken ct)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (request is null) return NotFound();

        var snapshotEntities = await db.CalculationAuditSnapshots
            .Where(s => s.RequestId == requestId)
            .OrderByDescending(s => s.CreatedUtc)
            .ToListAsync(ct);

        if (snapshotEntities.Count == 0)
        {
            return View(new CalculationAuditPageModel(requestId, request.RequestNumber, request.CompanyName, [], null, [], [], [], null));
        }

        // "Current" mirrors DossierCache/the delivery gate's own resolution exactly: the snapshot for the
        // request's latest completed ingestion run. If LatestCompletedIngestionRunId is null or no snapshot
        // matches it, there IS no current snapshot — this is an explicit, fail-closed state, never a silent
        // fallback to snapshotEntities[0] (the newest by CreatedUtc), which could be an abandoned or
        // superseded re-ingest attempt with nothing to do with what a live download would actually resolve
        // to.
        var currentSnapshot = snapshotEntities.FirstOrDefault(s => s.IngestionRunId == request.LatestCompletedIngestionRunId);
        // An explicit snapshotId is honored for historical browsing even when it isn't current — the
        // reviewer is deliberately looking at an old audit, not assuming it's live. An invalid/unmatched
        // snapshotId falls through to "no selection" below rather than silently substituting a different
        // snapshot than the one asked for.
        var selectedSnapshot = snapshotId is { } sid
            ? snapshotEntities.FirstOrDefault(s => s.CalculationAuditSnapshotId == sid)
            : currentSnapshot;

        var summaries = snapshotEntities
            .Select(s => new SnapshotSummary(s.CalculationAuditSnapshotId, s.IngestionRunId, s.AnalysisRunId, s.CreatedUtc,
                currentSnapshot is not null && s.CalculationAuditSnapshotId == currentSnapshot.CalculationAuditSnapshotId))
            .ToList();

        if (selectedSnapshot is null)
        {
            // The snapshot list is still shown (so a reviewer can deliberately pick a historical one to
            // browse), but no data is assumed to represent "the current state" — the empty Selected is
            // itself the signal to the view that nothing here can be treated as live.
            return View(new CalculationAuditPageModel(requestId, request.RequestNumber, request.CompanyName, summaries, null, [], [], [], null));
        }

        var selected = summaries.First(s => s.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId);

        var ledgerEntries = await db.CalculationLedgerEntries
            .Where(e => e.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId)
            .OrderBy(e => e.CalculationKey).ThenBy(e => e.Period)
            .ToListAsync(ct);
        var checkResults = await db.CalculationCheckResults
            .Where(c => c.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId)
            .OrderBy(c => c.CheckKey)
            .ToListAsync(ct);
        var discrepancies = await db.CalculationDiscrepancies
            .Where(d => d.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId)
            .OrderByDescending(d => d.CreatedUtc)
            .ToListAsync(ct);
        var aiAuditRun = await db.CalculationAiAuditRuns
            .FirstOrDefaultAsync(r => r.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId, ct);

        var discrepancyIds = discrepancies.Select(d => d.CalculationDiscrepancyId).ToList();
        var approvalsByDiscrepancy = (await db.CalculationDiscrepancyApprovals
                .Where(a => discrepancyIds.Contains(a.CalculationDiscrepancyId))
                .OrderBy(a => a.DecidedUtc)
                .ToListAsync(ct))
            .ToLookup(a => a.CalculationDiscrepancyId);
        var activeHoldDiscrepancyIds = (await db.CalculationArtifactHolds
                .Where(h => h.CalculationAuditSnapshotId == selectedSnapshot.CalculationAuditSnapshotId && h.IsActive && h.SourceDiscrepancyId != null)
                .Select(h => h.SourceDiscrepancyId!.Value)
                .ToListAsync(ct))
            .ToHashSet();
        var ledgerById = ledgerEntries.ToDictionary(e => e.CalculationLedgerEntryId);

        var discrepancyRows = discrepancies
            .Select(d => new DiscrepancyRow(
                d,
                ledgerById.GetValueOrDefault(d.PrimaryLedgerEntryId),
                approvalsByDiscrepancy[d.CalculationDiscrepancyId].ToList(),
                activeHoldDiscrepancyIds.Contains(d.CalculationDiscrepancyId)))
            .ToList();

        var model = new CalculationAuditPageModel(
            requestId, request.RequestNumber, request.CompanyName, summaries, selected,
            ledgerEntries, checkResults, discrepancyRows, aiAuditRun);
        return View(model);
    }

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/triage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Triage(long id, long requestId, string reviewerName, string? notes, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.TriageAsync(id, reviewerName, notes, ct));

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/confirm")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(long id, long requestId, string reviewerName, CalculationDiscrepancySeverity severity, string? notes, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.ConfirmAsync(id, reviewerName, severity, notes, ct));

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/reject")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long id, long requestId, string reviewerName, string reason, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.RejectAsync(id, reviewerName, reason, ct));

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/accept-exception")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AcceptException(long id, long requestId, string reviewerName, string exceptionReason, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.AcceptExceptionAsync(id, reviewerName, exceptionReason, ct));

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/mark-fixed-pending-reaudit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MarkFixedPendingReaudit(long id, long requestId, string reviewerName, string? notes, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.MarkFixedPendingReauditAsync(id, reviewerName, notes, ct));

    [HttpPost("/internal/calc-audit/discrepancy/{id:long}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Resolve(long id, long requestId, string reviewerName, string? notes, CancellationToken ct) =>
        await RunAndRedirect(requestId, workflow.ResolveAsync(id, reviewerName, notes, ct));

    /// <summary>Post-redirect-get for every action — the workflow service is the single source of truth for
    /// whether a transition applied; this controller never re-implements any of its rules.</summary>
    private async Task<IActionResult> RunAndRedirect(long requestId, Task<WorkflowResult> action)
    {
        var result = await action;
        if (!result.Success)
            TempData["CalcAuditError"] = result.Error;
        return RedirectToAction(nameof(Index), new { requestId });
    }
}
