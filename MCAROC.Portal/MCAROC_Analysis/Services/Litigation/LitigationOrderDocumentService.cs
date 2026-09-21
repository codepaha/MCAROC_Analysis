using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Retrieves, validates, retains and extracts text from one <see cref="LitigationCaseOrder"/>'s PDF
/// (#243/LIT-03) — see <see cref="LitigationOrderDocument"/>'s own remarks for the crash-safety, concurrency-
/// safety, retention and refresh design this implements. Structurally mirrors
/// <c>LitigationCasePersistenceService</c> closely on purpose: same lease/claim shape for crash safety, same
/// snapshot-style "ensure the row exists, then a worker processes it" split between admission and work.
///
/// <b>Every write to disk is per-claim, and every DB write that publishes or fails an attempt is
/// lease-token-fenced</b> — a stronger guarantee than the RowVersion-only protection <see
/// cref="TryClaimAsync"/> alone provides. RowVersion protects the DATABASE row from two attempts both
/// winning, but it says nothing about a FILE WRITE that happens outside any transaction: a worker whose lease
/// nominally expired while it was still (slowly) mid-download can finish and write its bytes to disk after a
/// takeover has already claimed, downloaded and published a — potentially different — file, and a
/// fixed/shared file path would let the straggler's write silently clobber the winner's file on disk even
/// though the winner's row (hash, etc.) is what the database still records. Two things close that gap: (1)
/// every write goes to a path keyed by this attempt's own <see cref="LitigationOrderDocument.LeaseToken"/>,
/// so two attempts can never write the same file; (2) the final "publish this as Downloaded" write (and every
/// Failed/Expired transition after a claim) is an <c>ExecuteUpdateAsync</c> explicitly guarded by
/// <c>(LitigationOrderDocumentId, LeaseToken == thisAttempt'sToken, LeaseExpiresUtc > now)</c> — mirroring
/// <c>LitigationSearchJobService.LeaseGuarded</c> exactly. A guard that matches 0 rows means this attempt's
/// claim is no longer authoritative (superseded by a takeover, or its own lease simply ran out); it deletes
/// only its own per-attempt file and touches nothing else — the row a takeover already published stands
/// untouched.</summary>
public sealed class LitigationOrderDocumentService(
    AppDbContext db, BprLitigationClient client, IStorageReservationManager reservations, PdfTextExtractor textExtractor,
    LitigationOrderDocumentQueue queue, IOptions<BprLitigationOptions> options, IWebHostEnvironment env,
    ILogger<LitigationOrderDocumentService> logger)
{
    private const int LeaseMinutes = 10; // one HTTP download + one PDF text extraction — generous but not job-length
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly BprLitigationOptions _opts = options.Value;

    // ── Processing ─────────────────────────────────────────────────────────────────────────────────
    // Admission (creating/refreshing a LitigationOrderDocument row) is deliberately NOT this service's job —
    // see LitigationCasePersistenceService.UpsertOrdersAsync, which admits documents inside the very same
    // transaction as the LitigationCaseOrder they belong to (and, for a re-surfaced existing order, the same
    // transaction as the refresh decision). This service only ever processes rows that already exist.

    public async Task DownloadAndExtractAsync(long orderDocumentId, CancellationToken ct)
    {
        var document = await db.LitigationOrderDocuments.Include(d => d.Order).ThenInclude(o => o!.Case)
            .FirstOrDefaultAsync(d => d.LitigationOrderDocumentId == orderDocumentId, ct);
        if (document is null || document.IsTerminal) return;
        if (document.Order?.Case is null)
        {
            logger.LogError(
                "Litigation order document {Id} has no reachable LitigationCaseOrder/LitigationCase — cannot resolve a RequestId.",
                orderDocumentId);
            return;
        }

        var retainedUntilUtc = document.RetainedUntilUtc;
        if (retainedUntilUtc <= DateTime.UtcNow)
        {
            await MarkExpiredAsync(document, "Retention window passed before this attempt started.", ct);
            return;
        }

        if (!await TryClaimAsync(document, ct)) return; // already owned by a still-live attempt
        var myLeaseToken = document.LeaseToken!.Value;
        var attemptCount = document.AttemptCount;
        var requestId = document.Order.Case.RequestId;
        var directory = Path.Combine(env.ContentRootPath, "App_Data", "Requests", requestId.ToString(), "litigation-orders");

        // Set only once the file is actually written — the finally block below deletes it if (and only if)
        // this attempt never successfully published it, covering both "lost the lease-guarded race" and any
        // unexpected exception after the write.
        string? myPath = null;
        var published = false;
        try
        {
            if (string.IsNullOrWhiteSpace(document.Order.PdfUrl))
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, "This order has no PdfUrl in the source report.", ct);
                return;
            }

            string token;
            try
            {
                token = await client.AuthenticateAsync(ct);
            }
            catch (BprLitigationException ex)
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, $"Authentication failed: {ex.Message}", ct);
                return;
            }

            var download = await client.DownloadOrderDocumentAsync(token, document.Order.PdfUrl, _opts.MaxOrderPdfBytes, ct);
            if (!download.Ok)
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, download.Error!, ct);
                return;
            }

            Directory.CreateDirectory(directory);
            // Keyed by this attempt's own lease token — never a fixed/shared path — so a stale straggler and
            // a takeover can never write the same file (see this type's own remarks).
            myPath = Path.Combine(directory, $"{orderDocumentId}-{myLeaseToken:N}.pdf");
            var reservationOwnerId = $"{orderDocumentId}:{myLeaseToken:N}";

            var reservation = await reservations.TryReserveAsync(
                "LitigationOrderDocument", reservationOwnerId, directory, download.Bytes!.LongLength, TimeSpan.FromMinutes(LeaseMinutes), ct);
            if (!reservation.Success)
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, $"Not enough disk space to retain this order: {reservation.Error}", ct);
                myPath = null; // nothing was ever written — no file for the finally block to clean up
                return;
            }

            try
            {
                await File.WriteAllBytesAsync(myPath, download.Bytes, ct);
            }
            finally
            {
                await reservations.ReleaseReservationsAsync("LitigationOrderDocument", reservationOwnerId, CancellationToken.None);
            }

            var fileHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(download.Bytes));
            var contentType = download.ContentType ?? "application/pdf";
            var downloadedUtc = DateTime.UtcNow;

            // Extraction failure never undoes a successful download — the file is retained regardless (that
            // is the primary contract); a null TextExtractionStatus just means extraction was never
            // successfully attempted, distinct from CorruptPdf/PasswordProtected (attempted, and failed).
            string? extractedText = null;
            FilingDocumentProcessingStatus? textStatus = null;
            TextExtractionMethod? textMethod = null;
            DateTime? extractedUtc = null;
            try
            {
                var tempDir = Path.Combine(directory, "tmp");
                var extraction = await textExtractor.ExtractAsync(myPath, tempDir, ct);
                extractedText = extraction.FullText;
                textStatus = extraction.Status;
                textMethod = extraction.Method;
                extractedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Text extraction threw for litigation order document {Id} — PDF retained, extraction left unattempted.",
                    orderDocumentId);
            }

            // The publish itself: guarded by this attempt's own lease token still being current AND
            // unexpired — never a plain SaveChangesAsync on the tracked entity. 0 rows means a takeover (or
            // simple lease timeout) has already superseded this attempt; its file must never become the
            // recorded one.
            var claimed = await LeaseGuarded(orderDocumentId, myLeaseToken).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, LitigationOrderDocumentStatus.Downloaded)
                .SetProperty(d => d.StoragePath, myPath)
                .SetProperty(d => d.FileSizeBytes, download.Bytes.LongLength)
                .SetProperty(d => d.FileHash, fileHash)
                .SetProperty(d => d.ContentType, contentType)
                .SetProperty(d => d.DownloadedUtc, downloadedUtc)
                .SetProperty(d => d.FailureReason, (string?)null)
                .SetProperty(d => d.ExtractedText, extractedText)
                .SetProperty(d => d.TextExtractionStatus, textStatus)
                .SetProperty(d => d.TextExtractionMethod, textMethod)
                .SetProperty(d => d.ExtractedUtc, extractedUtc), ct);

            if (claimed == 0)
            {
                logger.LogWarning(
                    "Litigation order document {Id} lost its lease before its download could be published — discarding this attempt's own file.",
                    orderDocumentId);
                return;
            }

            published = true;
            logger.LogInformation(
                "Litigation order document {Id} downloaded and extracted ({Bytes} bytes, {Method}).",
                orderDocumentId, download.Bytes.LongLength, textMethod);
        }
        finally
        {
            if (myPath is not null && !published)
                TryDeleteFile(myPath);
        }
    }

    /// <summary>Atomic claim: Pending, or InProgress with an expired lease, becomes InProgress under this
    /// attempt — identical shape to <c>LitigationCasePersistenceService.TryClaimAsync</c>. This is the one
    /// write in this type still protected by RowVersion/SaveChangesAsync rather than a lease-guarded
    /// ExecuteUpdateAsync: it is the write that MINTS the lease token every later write is guarded by, so
    /// there is no prior token to guard it with — RowVersion alone is sufficient here since nothing has
    /// happened yet that a stale writer could clobber.</summary>
    private async Task<bool> TryClaimAsync(LitigationOrderDocument document, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimable = document.Status == LitigationOrderDocumentStatus.Pending ||
            document.Status == LitigationOrderDocumentStatus.Failed ||
            (document.Status == LitigationOrderDocumentStatus.InProgress &&
                (document.LeaseExpiresUtc is null || document.LeaseExpiresUtc < now));
        if (!claimable) return false;

        document.Status = LitigationOrderDocumentStatus.InProgress;
        document.LeaseOwner = LeaseOwnerId;
        document.LeaseToken = Guid.NewGuid();
        document.LeaseExpiresUtc = now.AddMinutes(LeaseMinutes);
        document.AttemptCount++;

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation(
                "Lost a race claiming litigation order document {Id} — another attempt claimed it first.",
                document.LitigationOrderDocumentId);
            return false;
        }
    }

    /// <summary>The query every write after a claim must go through — requires this exact attempt's lease
    /// token and an unexpired lease, not just any tracked-entity SaveChanges. An ExecuteUpdateAsync against
    /// this query returning 0 means the row no longer matches (fenced out); callers must treat that as "stop
    /// touching this document," never as an ordinary failure to retry. Mirrors
    /// <c>LitigationSearchJobService.LeaseGuarded</c> exactly.</summary>
    private IQueryable<LitigationOrderDocument> LeaseGuarded(long documentId, Guid leaseToken)
    {
        var now = DateTime.UtcNow;
        return db.LitigationOrderDocuments.Where(d =>
            d.LitigationOrderDocumentId == documentId && d.LeaseToken == leaseToken && d.LeaseExpiresUtc != null && d.LeaseExpiresUtc > now);
    }

    /// <summary>A failed attempt is left <see cref="LitigationOrderDocumentStatus.Failed"/> (retryable) if
    /// still within the retention window, or moved straight to <see cref="LitigationOrderDocumentStatus.Expired"/>
    /// if not — never left silently InProgress or forgotten either way (the epic #239 requirement this whole
    /// type exists to satisfy: "no case is falsely reported as complete when an order download fails"). Lease-
    /// guarded exactly like the success path above: a fenced-out attempt must not resurrect a row a takeover
    /// has already moved on. A still-retryable failure schedules its own delayed re-attempt so it does not sit
    /// idle until the next app restart's recovery sweep.</summary>
    private async Task MarkFailedOrExpiredAsync(
        long documentId, Guid leaseToken, DateTime retainedUntilUtc, int attemptCount, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var newStatus = retainedUntilUtc <= now ? LitigationOrderDocumentStatus.Expired : LitigationOrderDocumentStatus.Failed;

        var claimed = await LeaseGuarded(documentId, leaseToken).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, newStatus)
            .SetProperty(d => d.FailureReason, reason), ct);
        if (claimed == 0)
        {
            logger.LogWarning(
                "Litigation order document {Id} lost its lease before its failure could be recorded — leaving it to whichever attempt now owns it.",
                documentId);
            return;
        }

        if (newStatus == LitigationOrderDocumentStatus.Failed)
        {
            var delayMinutes = Math.Min(180, 5 * Math.Pow(2, Math.Max(0, attemptCount - 1)));
            var readyUtc = now.AddMinutes(delayMinutes);
            var effectiveReadyUtc = readyUtc < retainedUntilUtc ? readyUtc : retainedUntilUtc;
            ScheduleRetry(documentId, effectiveReadyUtc, ct);
        }
    }

    /// <summary>Runs before any claim (no lease token exists yet), on a document whose retention window has
    /// already passed without ever being successfully downloaded — RowVersion alone is sufficient here for
    /// the same reason as <see cref="TryClaimAsync"/>: no file write or lease-guarded write has happened yet
    /// for this attempt to conflict with.</summary>
    private async Task MarkExpiredAsync(LitigationOrderDocument document, string reason, CancellationToken ct)
    {
        document.Status = LitigationOrderDocumentStatus.Expired;
        document.FailureReason = reason;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* someone else already advanced this row — leave it */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception)
        {
            // Best-effort cleanup of an attempt's own losing/abandoned file — never worth failing the whole
            // operation over a leftover temp file nothing will ever reference.
        }
    }

    // ── Recovery ───────────────────────────────────────────────────────────────────────────────────

    private void ScheduleRetry(long orderDocumentId, DateTime readyUtc, CancellationToken ct)
    {
        var delay = readyUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            queue.Enqueue(orderDocumentId);
            return;
        }

        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                capturedQueue.Enqueue(orderDocumentId);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — the row stays as-is; the next startup's recovery sweep re-enqueues it.
            }
        }, ct);
    }

    /// <summary>Re-enqueues every non-terminal document on startup, independent of anything else's status —
    /// same reasoning as <c>LitigationCasePersistenceService.RecoverStaleWorkAsync</c>. Includes <see
    /// cref="LitigationOrderDocumentStatus.Failed"/> rows here too (not just Pending/InProgress): a failed
    /// download is meant to be retried automatically up to its retention deadline, not just on an explicit
    /// refresh.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nonTerminal = await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Pending || d.Status == LitigationOrderDocumentStatus.Failed ||
                d.Status == LitigationOrderDocumentStatus.InProgress)
            .Select(d => new { d.LitigationOrderDocumentId, d.Status, d.LeaseExpiresUtc })
            .ToListAsync(ct);

        foreach (var d in nonTerminal)
        {
            if (d.Status == LitigationOrderDocumentStatus.InProgress && d.LeaseExpiresUtc is { } expires && expires > now)
                ScheduleRetry(d.LitigationOrderDocumentId, expires, ct);
            else
                queue.Enqueue(d.LitigationOrderDocumentId);
        }

        return nonTerminal.Count;
    }
}
