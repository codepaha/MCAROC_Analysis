using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.CalculationAssurance;

public record WorkflowResult(bool Success, string? Error)
{
    public static WorkflowResult Ok() => new(true, null);
    public static WorkflowResult Fail(string error) => new(false, error);
}

/// <summary>The reviewer decision workflow for a CalculationDiscrepancy (#164). Every decision is recorded
/// as an append-only CalculationDiscrepancyApproval row first — the durable audit trail — and the
/// discrepancy's own Status/Severity (a denormalized "current state" read model) is only advanced once
/// RequiredApprovals distinct reviewers have recorded that exact decision. RequiredApprovals defaults to 1
/// (today's single-reviewer policy), so in practice every transition below applies immediately after one
/// approval — the multi-approval gate exists and is tested (CalculationDiscrepancyWorkflowServiceTests)
/// entirely so that raising RequiredApprovals later is a pure data change, never a code change.
///
/// A deterministic check's own discrepancy is already Status=Confirmed at creation
/// (CalculationCheckRunnerService) — every action here except AcceptException/MarkFixedPendingReaudit/
/// Resolve is therefore only reachable for an AI-sourced candidate (Status starts Open).
///
/// Severity is never taken from the AI's own suggestion (CalculationDiscrepancy.ClaimSummary text only) —
/// a human reviewer supplies it explicitly at Confirm time, which is what makes "AI never imposes a hold"
/// hold structurally: nothing before this service ever writes a non-null Severity on an AiCandidate row.</summary>
public class CalculationDiscrepancyWorkflowService(AppDbContext db, ILogger<CalculationDiscrepancyWorkflowService> logger)
{
    public async Task<WorkflowResult> TriageAsync(long discrepancyId, string reviewerName, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("Only an AI-sourced candidate can be triaged — a deterministic check's discrepancy is already confirmed at creation.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Open)
            return WorkflowResult.Fail($"Cannot triage from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Triage, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Triage, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.Triaged;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>Confirms an AI candidate as a real discrepancy with a human-assigned severity. Requires a
    /// fresh deterministic reproduction first: the primary ledger entry's currently-stored value must still
    /// match what the candidate originally claimed as "actual" — if the underlying data has since changed
    /// (e.g. a re-ingest), the claim may no longer be live, and Confirm is refused rather than silently
    /// confirming a stale claim. Reaching RequiredApprovals with matching severity creates the matching
    /// CalculationArtifactHold for Critical/Material — the same auto-hold side effect a deterministic
    /// check's Triggered outcome gets, applied here at the human-decision step instead.</summary>
    public async Task<WorkflowResult> ConfirmAsync(long discrepancyId, string reviewerName, CalculationDiscrepancySeverity severity, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("A deterministic check's discrepancy is already confirmed at creation — there is nothing to confirm here.");
        if (discrepancy.Status is not (CalculationDiscrepancyStatus.Open or CalculationDiscrepancyStatus.Triaged))
            return WorkflowResult.Fail($"Cannot confirm from status {discrepancy.Status}.");

        var ledgerEntry = await db.CalculationLedgerEntries.FirstOrDefaultAsync(e => e.CalculationLedgerEntryId == discrepancy.PrimaryLedgerEntryId, ct);
        if (ledgerEntry is null) return WorkflowResult.Fail("The cited ledger entry no longer exists.");

        var stillMatches = ledgerEntry.ValueNumeric == discrepancy.ClaimedActualValue;
        var reproductionJson = JsonSerializer.Serialize(new
        {
            ledgerEntryId = ledgerEntry.CalculationLedgerEntryId,
            claimedActualValue = discrepancy.ClaimedActualValue,
            currentLedgerValue = ledgerEntry.ValueNumeric,
            matchesClaim = stillMatches,
            reproducedUtc = DateTime.UtcNow
        });
        if (!stillMatches)
            return WorkflowResult.Fail(
                "The ledger value has changed since this candidate was raised — the claim is no longer live. Reject this candidate instead (a fresh audit pass will raise a new one if the issue still exists).");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Confirm, reviewerName, notes, reproductionJson, severity, ct);
        if (!recorded.Success) return recorded;

        if (!await ConfirmApprovalsAgreeOnSeverityAsync(discrepancy, severity, ct))
            return WorkflowResult.Fail("Your assigned severity does not match a prior reviewer's on this same discrepancy — Confirm was recorded, but not yet applied. Resolve the disagreement before this transition can complete.");

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Confirm, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.Confirmed;
            discrepancy.Severity = severity;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;

            if (severity is CalculationDiscrepancySeverity.Critical or CalculationDiscrepancySeverity.Material)
            {
                db.CalculationArtifactHolds.Add(new CalculationArtifactHold
                {
                    CalculationAuditSnapshotId = discrepancy.CalculationAuditSnapshotId,
                    HoldReason = severity == CalculationDiscrepancySeverity.Critical
                        ? CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy
                        : CalculationArtifactHoldReason.ConfirmedMaterialDiscrepancyNoException,
                    IsActive = true,
                    SourceDiscrepancy = discrepancy,
                    CreatedUtc = DateTime.UtcNow
                });
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Discrepancy {DiscrepancyId} confirmed at severity {Severity} by {Reviewer}.", discrepancyId, severity, reviewerName);
        }
        return WorkflowResult.Ok();
    }

    public async Task<WorkflowResult> RejectAsync(long discrepancyId, string reviewerName, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return WorkflowResult.Fail("A rejection reason is required.");

        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("A deterministic check's discrepancy is authoritative and cannot be rejected.");
        if (discrepancy.Status is not (CalculationDiscrepancyStatus.Open or CalculationDiscrepancyStatus.Triaged))
            return WorkflowResult.Fail($"Cannot reject from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Reject, reviewerName, reason, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Reject, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.Rejected;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>No code path here — or anywhere else in this service — ever accepts Severity == Critical.
    /// A Critical discrepancy has no exception route at all, matching the issue's policy: hard hold until
    /// an actual correction + re-audit. Releases the associated hold, since a documented Material exception
    /// is meant to unblock delivery for the current snapshot.</summary>
    public async Task<WorkflowResult> AcceptExceptionAsync(long discrepancyId, string reviewerName, string exceptionReason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(exceptionReason)) return WorkflowResult.Fail("An exception reason is required.");

        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Confirmed)
            return WorkflowResult.Fail($"Cannot accept an exception from status {discrepancy.Status}.");
        if (discrepancy.Severity != CalculationDiscrepancySeverity.Material)
            return WorkflowResult.Fail("Only a Material discrepancy may be released via a documented exception. Critical has no exception route.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.AcceptException, reviewerName, exceptionReason, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.AcceptException, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.AcceptedAsSourceException;
            discrepancy.ExceptionReason = exceptionReason;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;

            await db.CalculationArtifactHolds
                .Where(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.IsActive, false)
                    .SetProperty(h => h.ReleasedUtc, DateTime.UtcNow)
                    .SetProperty(h => h.ReleasedByReviewerName, reviewerName)
                    .SetProperty(h => h.ReleaseNote, exceptionReason), ct);

            await db.SaveChangesAsync(ct);
            logger.LogInformation("Discrepancy {DiscrepancyId} released via documented Material exception by {Reviewer}.", discrepancyId, reviewerName);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>Bookkeeping only — the underlying source data was corrected, which produces a new
    /// IngestionRun/AnalysisRun/CalculationAuditSnapshot with its own fresh checks. This deliberately does
    /// NOT release the current snapshot's hold: that snapshot genuinely was defective and stays held
    /// forever; the customer's next download resolves to the new snapshot instead (DossierCache always
    /// resolves the latest completed run).</summary>
    public async Task<WorkflowResult> MarkFixedPendingReauditAsync(long discrepancyId, string reviewerName, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Confirmed)
            return WorkflowResult.Fail($"Cannot mark fixed-pending-reaudit from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.MarkFixedPendingReaudit, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.MarkFixedPendingReaudit, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.FixedPendingReaudit;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return WorkflowResult.Ok();
    }

    public async Task<WorkflowResult> ResolveAsync(long discrepancyId, string reviewerName, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.FixedPendingReaudit)
            return WorkflowResult.Fail($"Cannot resolve from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Resolve, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Resolve, ct))
        {
            discrepancy.Status = CalculationDiscrepancyStatus.Resolved;
            discrepancy.LastUpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>Records one reviewer's decision as a new append-only approval row. Refuses a second
    /// approval of the same action by the same reviewer — self-approval could otherwise trivially satisfy
    /// a future RequiredApprovals=2 gate by one person submitting twice.</summary>
    private async Task<WorkflowResult> RecordApprovalAsync(
        CalculationDiscrepancy discrepancy, CalculationDiscrepancyDecisionAction action, string reviewerName,
        string? notes, string? reproductionJson, CalculationDiscrepancySeverity? proposedSeverity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewerName)) return WorkflowResult.Fail("A reviewer name is required.");

        var alreadyDecidedByThisReviewer = await db.CalculationDiscrepancyApprovals.AnyAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == action && a.ReviewerName == reviewerName, ct);
        if (alreadyDecidedByThisReviewer)
            return WorkflowResult.Fail("You have already recorded this decision for this discrepancy.");

        var sequence = await db.CalculationDiscrepancyApprovals
            .CountAsync(a => a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == action, ct) + 1;

        db.CalculationDiscrepancyApprovals.Add(new CalculationDiscrepancyApproval
        {
            CalculationDiscrepancyId = discrepancy.CalculationDiscrepancyId,
            ApprovalSequence = sequence,
            DecisionAction = action,
            ReviewerName = reviewerName,
            ReviewerNotes = notes,
            DecidedUtc = DateTime.UtcNow,
            DeterministicReproductionResultJson = reproductionJson,
            ProposedSeverity = proposedSeverity
        });
        await db.SaveChangesAsync(ct);
        return WorkflowResult.Ok();
    }

    private async Task<bool> HasEnoughApprovalsAsync(CalculationDiscrepancy discrepancy, CalculationDiscrepancyDecisionAction action, CancellationToken ct)
    {
        var distinctReviewers = await db.CalculationDiscrepancyApprovals
            .Where(a => a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == action)
            .Select(a => a.ReviewerName)
            .Distinct()
            .CountAsync(ct);
        return distinctReviewers >= discrepancy.RequiredApprovals;
    }

    /// <summary>Every Confirm approval recorded so far for this discrepancy must agree on severity — a
    /// future dual-approval Confirm where two reviewers assign different severities must never silently
    /// pick one; it must withhold the transition instead. A no-op check today (RequiredApprovals=1 means
    /// this only ever compares one approval against itself).</summary>
    private async Task<bool> ConfirmApprovalsAgreeOnSeverityAsync(CalculationDiscrepancy discrepancy, CalculationDiscrepancySeverity severity, CancellationToken ct)
    {
        var distinctSeverities = await db.CalculationDiscrepancyApprovals
            .Where(a => a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId
                && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm)
            .Select(a => a.ProposedSeverity)
            .Distinct()
            .CountAsync(ct);
        return distinctSeverities <= 1;
    }
}
