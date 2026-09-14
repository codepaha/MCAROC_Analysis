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
/// hold structurally: nothing before this service ever writes a non-null Severity on an AiCandidate row.
///
/// Every status transition below is a WHERE-gated ExecuteUpdateAsync keyed on the expected prior status —
/// the same atomic-claim idiom AnalysisOrchestrator/CalculationAiAuditOrchestrator use — never a
/// load-then-mutate-then-SaveChangesAsync, so two concurrent callers reaching "enough approvals"
/// simultaneously can never both apply the same transition or its side effects (a hold, a hold release)
/// twice. A unique filtered index on CalculationArtifactHold.SourceDiscrepancyId is the DB-level backstop
/// behind that in case any future path ever bypasses this service.</summary>
public class CalculationDiscrepancyWorkflowService(AppDbContext db, ILogger<CalculationDiscrepancyWorkflowService> logger)
{
    public async Task<WorkflowResult> TriageAsync(long discrepancyId, string reviewerName, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("Only an AI-sourced candidate can be triaged — a deterministic check's discrepancy is already confirmed at creation.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Open)
            return WorkflowResult.Fail($"Cannot triage from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Triage, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Triage, ct))
        {
            await db.CalculationDiscrepancies
                .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && d.Status == CalculationDiscrepancyStatus.Open)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, CalculationDiscrepancyStatus.Triaged)
                    .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>Confirms an AI candidate as a real discrepancy with a human-assigned severity. Requires a
    /// fresh deterministic reproduction first: the primary ledger entry's currently-stored value must still
    /// match what the candidate originally claimed as "actual" — if the underlying data has since changed
    /// (e.g. a re-ingest), the claim may no longer be live, and Confirm is refused rather than silently
    /// confirming a stale claim.
    ///
    /// The discrepancy's own PendingConfirmSeverity is claimed atomically (compare-and-set from null) by
    /// whichever reviewer confirms first — every later Confirm approval must match that value or is
    /// refused outright, before it is ever persisted. This is what makes a severity disagreement always
    /// recoverable rather than a permanent deadlock: only ever one severity is actually recorded for this
    /// discrepancy's Confirm action, so any later reviewer can still complete the transition by agreeing
    /// with it (or the group can fall back to Reject if they decide the candidate isn't real after all).
    ///
    /// Reaching RequiredApprovals creates the matching CalculationArtifactHold for Critical/Material — the
    /// same auto-hold side effect a deterministic check's Triggered outcome gets, applied here at the
    /// human-decision step instead. The hold is only created by whichever concurrent call actually wins the
    /// atomic Open/Triaged→Confirmed transition, and the unique filtered index on SourceDiscrepancyId is
    /// the DB-level backstop even if that ever weren't enough.</summary>
    public async Task<WorkflowResult> ConfirmAsync(long discrepancyId, string reviewerName, CalculationDiscrepancySeverity severity, string? notes, CancellationToken ct)
    {
        // An undefined enum value (e.g. an unvalidated model-bound cast) must never reach the hold-branch
        // check below — Enum.IsDefined is what guarantees severity is exactly Minor/Material/Critical, so
        // "not Critical or Material" can only ever mean the real, intentional Minor case, never a bypass.
        if (!Enum.IsDefined(severity))
            return WorkflowResult.Fail($"'{severity}' is not a recognized severity.");

        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("A deterministic check's discrepancy is already confirmed at creation — there is nothing to confirm here.");
        if (discrepancy.Status is not (CalculationDiscrepancyStatus.Open or CalculationDiscrepancyStatus.Triaged))
            return WorkflowResult.Fail($"Cannot confirm from status {discrepancy.Status}.");

        var ledgerEntry = await db.CalculationLedgerEntries.AsNoTracking().FirstOrDefaultAsync(e => e.CalculationLedgerEntryId == discrepancy.PrimaryLedgerEntryId, ct);
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

        // Atomic compare-and-set: only the first caller to reach this line for this discrepancy actually
        // changes PendingConfirmSeverity from null; every other caller (racing or sequential) reads back
        // whichever value won, deterministically. This closes the race a plain "query existing severities,
        // then insert if they match" check-then-act sequence could not.
        var claimedSeverity = await db.CalculationDiscrepancies
            .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && d.PendingConfirmSeverity == null)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PendingConfirmSeverity, severity), ct) == 1
            ? severity
            : await db.CalculationDiscrepancies.Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId)
                .Select(d => d.PendingConfirmSeverity).FirstAsync(ct);

        if (claimedSeverity != severity)
            return WorkflowResult.Fail(
                $"A prior reviewer already confirmed this discrepancy at severity {claimedSeverity} — your {severity} assignment conflicts and was not recorded. " +
                "Agree with the existing severity to add your approval, or use Reject to send this candidate back instead.");

        // Idempotent retry: if this reviewer already recorded a matching Confirm approval — most likely
        // because a prior attempt's transition/hold transaction below failed and rolled back after the
        // approval had already committed — skip straight to retrying the transition rather than failing
        // on "already decided". A genuinely different severity from the same reviewer is still refused.
        var existingApproval = await db.CalculationDiscrepancyApprovals.AsNoTracking().FirstOrDefaultAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId
            && a.DecisionAction == CalculationDiscrepancyDecisionAction.Confirm && a.ReviewerName == reviewerName, ct);
        if (existingApproval is not null)
        {
            if (existingApproval.ProposedSeverity != severity)
                return WorkflowResult.Fail("You have already confirmed this discrepancy at a different severity and cannot change your decision here.");
        }
        else
        {
            var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Confirm, reviewerName, notes, reproductionJson, severity, ct);
            if (!recorded.Success) return recorded;
        }

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Confirm, ct))
        {
            // The status transition and the hold it requires must commit together or not at all — a
            // Confirmed Critical/Material discrepancy without an active hold would silently defeat the
            // whole delivery-gate invariant, with no way back in (Confirm can't run again once Status has
            // already left Open/Triaged). Any failure here — not just a unique-index race — rolls back
            // both, and since the reviewer's own approval was already committed above (in its own,
            // separate, already-successful transaction), a plain retry of Confirm with the same
            // reviewer/severity picks up exactly here again without needing to re-approve.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var transitioned = await db.CalculationDiscrepancies
                    .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId
                        && (d.Status == CalculationDiscrepancyStatus.Open || d.Status == CalculationDiscrepancyStatus.Triaged))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, CalculationDiscrepancyStatus.Confirmed)
                        .SetProperty(d => d.Severity, severity)
                        .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);

                // Only the caller that actually won the transition creates the hold — a concurrent second
                // caller that also observed "enough approvals" sees transitioned == 0 here and does
                // nothing further, so exactly one hold is ever created per discrepancy.
                if (transitioned == 1 && severity is CalculationDiscrepancySeverity.Critical or CalculationDiscrepancySeverity.Material)
                {
                    db.CalculationArtifactHolds.Add(new CalculationArtifactHold
                    {
                        CalculationAuditSnapshotId = discrepancy.CalculationAuditSnapshotId,
                        HoldReason = severity == CalculationDiscrepancySeverity.Critical
                            ? CalculationArtifactHoldReason.ConfirmedCriticalDiscrepancy
                            : CalculationArtifactHoldReason.ConfirmedMaterialDiscrepancyNoException,
                        IsActive = true,
                        SourceDiscrepancyId = discrepancy.CalculationDiscrepancyId,
                        CreatedUtc = DateTime.UtcNow
                    });
                    await db.SaveChangesAsync(ct);
                }

                await transaction.CommitAsync(ct);

                if (transitioned == 1)
                    logger.LogInformation("Discrepancy {DiscrepancyId} confirmed at severity {Severity} by {Reviewer}.", discrepancyId, severity, reviewerName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to commit the Confirm transition/hold for discrepancy {DiscrepancyId} — rolled back.", discrepancyId);
                return WorkflowResult.Fail("Could not complete the confirmation due to a database error — please retry.");
            }
        }
        return WorkflowResult.Ok();
    }

    public async Task<WorkflowResult> RejectAsync(long discrepancyId, string reviewerName, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) return WorkflowResult.Fail("A rejection reason is required.");

        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.SourceType != CalculationDiscrepancySourceType.AiCandidate)
            return WorkflowResult.Fail("A deterministic check's discrepancy is authoritative and cannot be rejected.");
        if (discrepancy.Status is not (CalculationDiscrepancyStatus.Open or CalculationDiscrepancyStatus.Triaged))
            return WorkflowResult.Fail($"Cannot reject from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Reject, reviewerName, reason, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Reject, ct))
        {
            await db.CalculationDiscrepancies
                .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId
                    && (d.Status == CalculationDiscrepancyStatus.Open || d.Status == CalculationDiscrepancyStatus.Triaged))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, CalculationDiscrepancyStatus.Rejected)
                    .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);
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

        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Confirmed)
            return WorkflowResult.Fail($"Cannot accept an exception from status {discrepancy.Status}.");
        if (discrepancy.Severity != CalculationDiscrepancySeverity.Material)
            return WorkflowResult.Fail("Only a Material discrepancy may be released via a documented exception. Critical has no exception route.");

        // Idempotent retry, mirroring Confirm above — a prior attempt's transaction may have already
        // recorded this reviewer's approval and then failed to commit the transition/hold-release. The
        // retry must supply the exact same reason as what was durably recorded: applying a freshly-typed
        // reason B to a transition while the append-only audit trail still says reason A would let the
        // current state and its own justification silently disagree.
        var existingApproval = await db.CalculationDiscrepancyApprovals.AsNoTracking().FirstOrDefaultAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId
            && a.DecisionAction == CalculationDiscrepancyDecisionAction.AcceptException && a.ReviewerName == reviewerName, ct);
        if (existingApproval is not null)
        {
            if (!string.Equals(existingApproval.ReviewerNotes?.Trim(), exceptionReason.Trim(), StringComparison.Ordinal))
                return WorkflowResult.Fail(
                    "You already recorded a different exception reason for this discrepancy and cannot change it here. Resubmit with the exact original reason to retry.");
        }
        else
        {
            var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.AcceptException, reviewerName, exceptionReason, null, null, ct);
            if (!recorded.Success) return recorded;
        }

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.AcceptException, ct))
        {
            // Same atomicity requirement as Confirm's transition+hold, in reverse: an accepted exception
            // must never exist while its hold is still active — the whole point of accepting the exception
            // is to release delivery. Any failure here rolls back both the status transition and the
            // (attempted) hold release together.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                var transitioned = await db.CalculationDiscrepancies
                    .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && d.Status == CalculationDiscrepancyStatus.Confirmed)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.Status, CalculationDiscrepancyStatus.AcceptedAsSourceException)
                        .SetProperty(d => d.ExceptionReason, exceptionReason)
                        .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);

                if (transitioned == 1)
                {
                    await db.CalculationArtifactHolds
                        .Where(h => h.SourceDiscrepancyId == discrepancy.CalculationDiscrepancyId && h.IsActive)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(h => h.IsActive, false)
                            .SetProperty(h => h.ReleasedUtc, DateTime.UtcNow)
                            .SetProperty(h => h.ReleasedByReviewerName, reviewerName)
                            .SetProperty(h => h.ReleaseNote, exceptionReason), ct);
                }

                await transaction.CommitAsync(ct);

                if (transitioned == 1)
                    logger.LogInformation("Discrepancy {DiscrepancyId} released via documented Material exception by {Reviewer}.", discrepancyId, reviewerName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to commit the AcceptException transition/hold-release for discrepancy {DiscrepancyId} — rolled back.", discrepancyId);
                return WorkflowResult.Fail("Could not complete the exception acceptance due to a database error — please retry.");
            }
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
        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.Confirmed)
            return WorkflowResult.Fail($"Cannot mark fixed-pending-reaudit from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.MarkFixedPendingReaudit, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.MarkFixedPendingReaudit, ct))
        {
            await db.CalculationDiscrepancies
                .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && d.Status == CalculationDiscrepancyStatus.Confirmed)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, CalculationDiscrepancyStatus.FixedPendingReaudit)
                    .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);
        }
        return WorkflowResult.Ok();
    }

    public async Task<WorkflowResult> ResolveAsync(long discrepancyId, string reviewerName, string? notes, CancellationToken ct)
    {
        var discrepancy = await db.CalculationDiscrepancies.AsNoTracking().FirstOrDefaultAsync(d => d.CalculationDiscrepancyId == discrepancyId, ct);
        if (discrepancy is null) return WorkflowResult.Fail("Discrepancy not found.");
        if (discrepancy.Status != CalculationDiscrepancyStatus.FixedPendingReaudit)
            return WorkflowResult.Fail($"Cannot resolve from status {discrepancy.Status}.");

        var recorded = await RecordApprovalAsync(discrepancy, CalculationDiscrepancyDecisionAction.Resolve, reviewerName, notes, null, null, ct);
        if (!recorded.Success) return recorded;

        if (await HasEnoughApprovalsAsync(discrepancy, CalculationDiscrepancyDecisionAction.Resolve, ct))
        {
            await db.CalculationDiscrepancies
                .Where(d => d.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && d.Status == CalculationDiscrepancyStatus.FixedPendingReaudit)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, CalculationDiscrepancyStatus.Resolved)
                    .SetProperty(d => d.LastUpdatedUtc, DateTime.UtcNow), ct);
        }
        return WorkflowResult.Ok();
    }

    /// <summary>Records one reviewer's decision as a new append-only approval row. A unique index on
    /// (CalculationDiscrepancyId, DecisionAction, ReviewerName) is the actual DB-enforced guarantee behind
    /// "a reviewer can't approve the same decision twice" — the upfront AnyAsync check below is only a
    /// fast, friendly path for the overwhelmingly common non-racing case; a concurrent double-submit is
    /// caught by the constraint in the catch block instead. ApprovalSequence is retried on a collision
    /// (two different reviewers racing to be "sequence 1"), bounded to a handful of attempts since a
    /// genuine collision this many times in a row is not a realistic contention level for this feature.</summary>
    private async Task<WorkflowResult> RecordApprovalAsync(
        CalculationDiscrepancy discrepancy, CalculationDiscrepancyDecisionAction action, string reviewerName,
        string? notes, string? reproductionJson, CalculationDiscrepancySeverity? proposedSeverity, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reviewerName)) return WorkflowResult.Fail("A reviewer name is required.");

        var alreadyDecidedByThisReviewer = await db.CalculationDiscrepancyApprovals.AnyAsync(a =>
            a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == action && a.ReviewerName == reviewerName, ct);
        if (alreadyDecidedByThisReviewer)
            return WorkflowResult.Fail("You have already recorded this decision for this discrepancy.");

        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
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

            try
            {
                await db.SaveChangesAsync(ct);
                return WorkflowResult.Ok();
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                db.ChangeTracker.Clear();

                // Distinguish which constraint fired: a genuine repeat by this same reviewer (stop
                // retrying, report clearly) vs. a transient ApprovalSequence collision with a different,
                // concurrently-racing reviewer (retry with a freshly recomputed sequence).
                var stillAlreadyDecided = await db.CalculationDiscrepancyApprovals.AnyAsync(a =>
                    a.CalculationDiscrepancyId == discrepancy.CalculationDiscrepancyId && a.DecisionAction == action && a.ReviewerName == reviewerName, ct);
                if (stillAlreadyDecided)
                    return WorkflowResult.Fail("You have already recorded this decision for this discrepancy.");
            }
        }
        return WorkflowResult.Fail("Could not record your decision due to concurrent activity — please try again.");
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

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
