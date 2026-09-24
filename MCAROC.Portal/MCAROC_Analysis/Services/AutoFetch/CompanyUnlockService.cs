using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

public enum UnlockOutcome
{
    /// <summary>This call spent the credit and the tool now reports the company unlocked.</summary>
    Unlocked,
    /// <summary>Someone else had already unlocked it — nothing spent.</summary>
    AlreadyUnlocked,
    /// <summary>No usable approval (and auto-unlock is off) — nothing spent; keep waiting.</summary>
    NoApproval,
    /// <summary>The tool's preview names a different company — nothing spent; needs a human.</summary>
    IdentityMismatch,
    /// <summary>MCA maintenance, or another unlock for this company is in flight — nothing spent; try later.</summary>
    Deferred,
    /// <summary>The paid call was attempted and failed or couldn't be confirmed — the credit may have been spent.</summary>
    Failed
}

public sealed record UnlockResult(UnlockOutcome Outcome, string Message);

/// <summary>The approval-gated paid unlock (#266, docs/pipeline-automation-plan.md §5.7). The only code that
/// spends a credit on the reference tool, and it does so only when all of these hold, in this order:
/// the tool still reports the company locked (anyone's existing unlock is adopted for free); an unconsumed
/// approval exists for the company (or auto-unlock is explicitly on); the tool's free preview names the same
/// CIN/LLPIN; the MCA portal isn't in maintenance; and the paid-call admission and the approval's consumption
/// commit together — so two executors can never spend on one approval. The admission is committed before the
/// paid call (fail closed: from that point the credit may be spent) and the call is never retried
/// automatically; a failure needs a fresh approval.
///
/// Approvals don't expire (owner decision, plan §9 #12) and one covers every request waiting on the company.
/// Because they don't expire, every open approval for a company is retired the moment it is found unlocked —
/// otherwise a year-old approval could silently authorize the next unlock.</summary>
public sealed class CompanyUnlockService(
    AppDbContext db, ReferenceToolClient client, IPaidCallAdmission admission, IOptionsMonitor<PipelineOptions> pipeline,
    TimeProvider time, ILogger<CompanyUnlockService> logger)
{
    /// <summary>How long an approval waits behind an unlock attempt already running for the company.</summary>
    public static readonly TimeSpan ApprovalWait = TimeSpan.FromSeconds(60);

    /// <summary>Records an approval for the company the request is for, or returns null when none is needed
    /// because the tool already reports the company unlocked. Idempotent: an open approval for the company is
    /// returned rather than duplicated.
    ///
    /// Runs under the same per-company fence as <see cref="ExecuteAsync"/>, so an approval can never be created
    /// while an unlock attempt is in flight — neither slipping in after that attempt's final retirement (a stale,
    /// never-expiring approval that could authorize a spend once the unlock lapses) nor being consumed
    /// half-way. Once it holds the fence it checks the tool's live state, so an approval requested just after a
    /// successful unlock is refused rather than stored. The fence also serialises concurrent approvals.</summary>
    /// <exception cref="InvalidOperationException">An unlock attempt for the company is still running after
    /// <see cref="ApprovalWait"/>.</exception>
    public async Task<UnlockApproval?> ApproveAsync(long requestId, string approvedBy, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required to approve spending a credit.", nameof(reason));
        if (string.IsNullOrWhiteSpace(approvedBy)) throw new ArgumentException("The approver must be known.", nameof(approvedBy));
        var job = await db.AutoFetchJobs.AsNoTracking().Where(j => j.RequestId == requestId).Select(j => new { j.Cin, j.Bid }).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Only an auto-fetch request can have its company unlocked.");
        var identifier = job.Cin.Trim().ToUpperInvariant();

        await using var fence = await SqlSessionLock.TryAcquireAsync(db, ExecutionFence(identifier), ct, ApprovalWait)
            ?? throw new InvalidOperationException("An unlock attempt for this company is still in progress; try again in a moment.");

        if ((await client.GetAssetStatusAsync(job.Bid, ct)).AddedAt is not null)
        {
            await RetireOpenApprovalsAsync(identifier, ct);
            return null;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var open = await OpenApprovals(identifier, now).OrderBy(a => a.UnlockApprovalId).FirstOrDefaultAsync(ct);
        if (open is not null) return open;

        var approval = new UnlockApproval
        {
            Identifier = identifier, RequestId = requestId, ApprovedBy = approvedBy.Trim(), Reason = reason.Trim(),
            ApprovedUtc = now, ExpiresUtc = DateTime.MaxValue
        };
        db.UnlockApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("Unlock of {Identifier} approved by {ApprovedBy} (request {RequestId}): {Reason}", identifier, approval.ApprovedBy, requestId, approval.Reason);
        return approval;
    }

    private static string ExecutionFence(string identifier) => $"MCAROC:UnlockExecution:{identifier}";

    /// <summary>Free checks only: the text shown on the waiting job and board, or null when the preview names a
    /// different company (which must never be unlocked on this request's say-so).</summary>
    public async Task<(bool IdentityMatches, string Message)> DescribeLockedAsync(string identifier, string bid, CancellationToken ct)
    {
        identifier = identifier.Trim().ToUpperInvariant();
        var preview = await client.GetCompanyPreviewAsync(bid, ct);
        if (!string.Equals(preview.Cin, identifier, StringComparison.OrdinalIgnoreCase))
            return (false, $"IDENTITY_MISMATCH: the reference tool's preview for this company is CIN '{preview.Cin ?? "none"}', not '{identifier}'. Nothing will be unlocked for this request.");
        var details = string.Join(", ", new[] { preview.Status, preview.State }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return (true, $"UNLOCK_APPROVAL_REQUIRED: {preview.LegalName ?? identifier}{(details.Length > 0 ? $" ({details})" : "")} is locked in the reference tool. Unlocking costs 1 credit and needs an approval.");
    }

    public async Task<UnlockResult> ExecuteAsync(string identifier, string bid, long requestId, CancellationToken ct)
    {
        identifier = identifier.Trim().ToUpperInvariant();

        // One unlock attempt per company at a time, held from the locked-check to the paid call's verification.
        // Committing the admission frees its scope before addAsset returns, and manual admissions skip the
        // cooldown, so without this a fresh approval arriving mid-call could be admitted and spend again. A
        // caller that finds it held backs off; by its next try the company is unlocked (adopted, approval
        // retired) or the attempt failed (approvals retired).
        await using var fence = await SqlSessionLock.TryAcquireAsync(db, ExecutionFence(identifier), ct);
        if (fence is null)
            return new UnlockResult(UnlockOutcome.Deferred, "Another unlock attempt for this company is in progress.");

        var asset = await client.GetAssetStatusAsync(bid, ct);
        if (asset.AddedAt is not null)
        {
            await RetireOpenApprovalsAsync(identifier, ct);
            return new UnlockResult(UnlockOutcome.AlreadyUnlocked, "The company is already unlocked in the reference tool; nothing was spent.");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var approval = await OpenApprovals(identifier, now).OrderBy(a => a.UnlockApprovalId).FirstOrDefaultAsync(ct);
        var auto = approval is null && pipeline.CurrentValue.AutoUnlock.Enabled;
        if (approval is null && !auto)
            return new UnlockResult(UnlockOutcome.NoApproval, "Waiting for an approval to spend 1 credit on the unlock.");
        if (asset.TeamId is not { } teamId)
            return new UnlockResult(UnlockOutcome.Deferred, "The reference tool didn't report which team to charge the unlock to.");

        var preview = await client.GetCompanyPreviewAsync(bid, ct);
        if (!string.Equals(preview.Cin, identifier, StringComparison.OrdinalIgnoreCase))
            return new UnlockResult(UnlockOutcome.IdentityMismatch,
                $"IDENTITY_MISMATCH: the reference tool's preview is CIN '{preview.Cin ?? "none"}', not '{identifier}'. Nothing was spent.");
        if (!await client.IsMcaAvailableForUnlockAsync(ct))
            return new UnlockResult(UnlockOutcome.Deferred, "The MCA portal is under maintenance; the unlock will be attempted once it is back.");

        var clientId = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => (long?)r.ClientId).FirstOrDefaultAsync(ct);
        var approvalId = approval?.UnlockApprovalId;
        var admitted = await admission.TryAdmitAsync(new PaidCallAdmissionRequest(
            PaidCallKind.ReferenceUnlock, PaidCallScopeKeys.ReferenceUnlock(identifier),
            auto ? PaidCallTrigger.Auto : PaidCallTrigger.Manual, requestId, clientId,
            WithinTransaction: approvalId is { } id ? (tdb, admissionId, c) => ConsumeAsync(tdb, id, admissionId, now, c) : null), ct);
        if (!admitted.Admitted)
            return new UnlockResult(UnlockOutcome.Deferred, admitted.Denial switch
            {
                AdmissionDenial.PreconditionFailed => "The approval was already used by another unlock attempt.",
                AdmissionDenial.InFlight => "Another unlock for this company is in progress.",
                AdmissionDenial.CostCapReached => "Today's automatic unlock limit has been reached.",
                _ => "The company was unlocked within the last 12 months."
            });

        // Fail closed: from here the credit may be spent, so the ledger records it before the call is made.
        await admission.CommitAsync(admitted.AdmissionId!.Value, now, ct);
        logger.LogWarning("Spending 1 reference-tool credit to unlock {Identifier} ({Trigger}, admission {AdmissionId}, approval {ApprovalId})",
            identifier, auto ? "auto" : "approved", admitted.AdmissionId, approvalId);
        try
        {
            try
            {
                await client.AddAssetAsync(teamId, bid, preview.LegalName ?? identifier, ct);
            }
            catch (Exception ex) when (ex is ReferenceToolException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Unlock call for {Identifier} failed after the admission was committed", identifier);
                return new UnlockResult(UnlockOutcome.Failed,
                    $"The unlock call failed ({ex.Message}). The credit may have been spent — check the reference tool before approving again.");
            }

            var after = await client.GetAssetStatusAsync(bid, ct);
            if (after.AddedAt is null)
                return new UnlockResult(UnlockOutcome.Failed,
                    "The reference tool accepted the unlock but still reports the company locked. Check the tool before approving again.");
            return new UnlockResult(UnlockOutcome.Unlocked, $"Unlocked in the reference tool on {after.AddedAt:u} (1 credit).");
        }
        finally
        {
            // Whatever happened after the ledger commit — success, a failed or unconfirmed call, an unexpected
            // exception, cancellation — no approval may outlive this attempt: an outcome we can't confirm needs a
            // fresh, deliberate approval. The fence is still held, so none can be created in between.
            try
            {
                await RetireOpenApprovalsAsync(identifier, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not retire open unlock approvals for {Identifier} after an unlock attempt", identifier);
            }
        }
    }

    private IQueryable<UnlockApproval> OpenApprovals(string identifier, DateTime now) =>
        db.UnlockApprovals.Where(a => a.Identifier == identifier && a.ConsumedAdmissionId == null && a.ExpiresUtc > now);

    /// <summary>Exactly one row, or the admission rolls back.</summary>
    private static async Task<bool> ConsumeAsync(AppDbContext tdb, long approvalId, long admissionId, DateTime now, CancellationToken ct) =>
        await tdb.UnlockApprovals
            .Where(a => a.UnlockApprovalId == approvalId && a.ConsumedAdmissionId == null && a.ExpiresUtc > now)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ConsumedAdmissionId, admissionId), ct) == 1;

    private Task RetireOpenApprovalsAsync(string identifier, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return OpenApprovals(identifier, now).ExecuteUpdateAsync(s => s.SetProperty(a => a.ExpiresUtc, now), ct);
    }
}
