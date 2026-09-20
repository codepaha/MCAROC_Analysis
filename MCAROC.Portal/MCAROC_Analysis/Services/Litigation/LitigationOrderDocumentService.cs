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
/// snapshot-style "ensure the row exists, then a worker processes it" split between admission and work.</summary>
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

        if (document.RetainedUntilUtc <= DateTime.UtcNow)
        {
            await MarkExpiredAsync(document, "Retention window passed before this attempt started.", ct);
            return;
        }

        if (!await TryClaimAsync(document, ct)) return; // already owned by a still-live attempt

        try
        {
            if (string.IsNullOrWhiteSpace(document.Order.PdfUrl))
            {
                await MarkFailedOrExpiredAsync(document, "This order has no PdfUrl in the source report.", ct);
                return;
            }

            string token;
            try
            {
                token = await client.AuthenticateAsync(ct);
            }
            catch (BprLitigationException ex)
            {
                await MarkFailedOrExpiredAsync(document, $"Authentication failed: {ex.Message}", ct);
                return;
            }

            var download = await client.DownloadOrderDocumentAsync(token, document.Order.PdfUrl, _opts.MaxOrderPdfBytes, ct);
            if (!download.Ok)
            {
                await MarkFailedOrExpiredAsync(document, download.Error!, ct);
                return;
            }

            var requestId = document.Order.Case.RequestId;
            var directory = Path.Combine(env.ContentRootPath, "App_Data", "Requests", requestId.ToString(), "litigation-orders");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{document.LitigationOrderDocumentId}.pdf");

            var reservation = await reservations.TryReserveAsync(
                "LitigationOrderDocument", document.LitigationOrderDocumentId.ToString(), directory,
                download.Bytes!.LongLength, TimeSpan.FromMinutes(LeaseMinutes), ct);
            if (!reservation.Success)
            {
                await MarkFailedOrExpiredAsync(document, $"Not enough disk space to retain this order: {reservation.Error}", ct);
                return;
            }

            try
            {
                await File.WriteAllBytesAsync(path, download.Bytes, ct);
            }
            finally
            {
                // Released the instant the write finishes (success or failure) — unlike AutoFetch's whole-job
                // reservation, one order document is a single short-lived write with nothing else pending
                // under the same owner id, so there is nothing left to protect once it returns.
                await reservations.ReleaseReservationsAsync(
                    "LitigationOrderDocument", document.LitigationOrderDocumentId.ToString(), CancellationToken.None);
            }

            document.Status = LitigationOrderDocumentStatus.Downloaded;
            document.StoragePath = path;
            document.FileSizeBytes = download.Bytes.LongLength;
            document.FileHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(download.Bytes));
            document.ContentType = download.ContentType ?? "application/pdf";
            document.DownloadedUtc = DateTime.UtcNow;
            document.FailureReason = null;

            // Extraction failure never undoes a successful download — the file is retained regardless (that
            // is the primary contract); TextExtractionStatus staying null just means extraction was never
            // successfully attempted, distinct from CorruptPdf/PasswordProtected (attempted, and failed).
            try
            {
                var tempDir = Path.Combine(directory, "tmp");
                var extraction = await textExtractor.ExtractAsync(path, tempDir, ct);
                document.ExtractedText = extraction.FullText;
                document.TextExtractionStatus = extraction.Status;
                document.TextExtractionMethod = extraction.Method;
                document.ExtractedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Text extraction threw for litigation order document {Id} — PDF retained, extraction left unattempted.",
                    orderDocumentId);
            }

            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Litigation order document {Id} downloaded and extracted ({Bytes} bytes, {Method}).",
                orderDocumentId, document.FileSizeBytes, document.TextExtractionMethod);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogWarning(
                "Litigation order document {Id} lost its claim mid-processing — another attempt has taken over.", orderDocumentId);
        }
    }

    /// <summary>Atomic claim: Pending, or InProgress with an expired lease, becomes InProgress under this
    /// attempt — identical shape to <c>LitigationCasePersistenceService.TryClaimAsync</c>.</summary>
    private async Task<bool> TryClaimAsync(LitigationOrderDocument document, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimable = document.Status == LitigationOrderDocumentStatus.Pending ||
            (document.Status == LitigationOrderDocumentStatus.Failed) ||
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

    /// <summary>A failed attempt is left <see cref="LitigationOrderDocumentStatus.Failed"/> (retryable) if
    /// still within the retention window, or moved straight to <see cref="LitigationOrderDocumentStatus.Expired"/>
    /// if not — never left silently InProgress or forgotten either way (the epic #239 requirement this whole
    /// type exists to satisfy: "no case is falsely reported as complete when an order download fails"). A
    /// still-retryable failure schedules its own delayed re-attempt so it does not sit idle until the next app
    /// restart's recovery sweep.</summary>
    private async Task MarkFailedOrExpiredAsync(LitigationOrderDocument document, string reason, CancellationToken ct)
    {
        document.FailureReason = reason;
        var now = DateTime.UtcNow;
        if (document.RetainedUntilUtc <= now)
        {
            document.Status = LitigationOrderDocumentStatus.Expired;
        }
        else
        {
            document.Status = LitigationOrderDocumentStatus.Failed;
        }

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { return; /* someone else already advanced this row — leave it */ }

        if (document.Status == LitigationOrderDocumentStatus.Failed)
        {
            var delayMinutes = Math.Min(180, 5 * Math.Pow(2, Math.Max(0, document.AttemptCount - 1)));
            var readyUtc = now.AddMinutes(delayMinutes);
            var effectiveReadyUtc = readyUtc < document.RetainedUntilUtc ? readyUtc : document.RetainedUntilUtc;
            ScheduleRetry(document.LitigationOrderDocumentId, effectiveReadyUtc, ct);
        }
    }

    private async Task MarkExpiredAsync(LitigationOrderDocument document, string reason, CancellationToken ct)
    {
        document.Status = LitigationOrderDocumentStatus.Expired;
        document.FailureReason = reason;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* someone else already advanced this row — leave it */ }
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
