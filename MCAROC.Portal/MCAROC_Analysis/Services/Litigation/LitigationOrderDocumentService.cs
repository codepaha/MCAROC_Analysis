using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Orders;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
public sealed class LitigationOrderDocumentService
{
    private const int LeaseMinutes = 10; // one HTTP download + one PDF text extraction — generous but not job-length
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly AppDbContext db;
    private readonly IEnumerable<IOrderSourceClient> _sourceClients;
    private readonly IStorageReservationManager reservations;
    private readonly PdfTextExtractor textExtractor;
    private readonly LitigationOrderDocumentQueue queue;
    private readonly LitigationOrderChunkingQueue chunkingQueue;
    private readonly BprLitigationOptions _opts;
    private readonly IWebHostEnvironment env;
    private readonly ILogger<LitigationOrderDocumentService> logger;

    public LitigationOrderDocumentService(
        AppDbContext db,
        IEnumerable<IOrderSourceClient> sourceClients,
        IStorageReservationManager reservations,
        PdfTextExtractor textExtractor,
        LitigationOrderDocumentQueue queue,
        LitigationOrderChunkingQueue chunkingQueue,
        IOptions<BprLitigationOptions> options,
        IWebHostEnvironment env,
        ILogger<LitigationOrderDocumentService> logger)
    {
        this.db = db;
        _sourceClients = sourceClients;
        this.reservations = reservations;
        this.textExtractor = textExtractor;
        this.queue = queue;
        this.chunkingQueue = chunkingQueue;
        _opts = options.Value;
        this.env = env;
        this.logger = logger;
    }

    internal LitigationOrderDocumentService(
        AppDbContext db,
        BprLitigationClient client,
        IStorageReservationManager reservations,
        PdfTextExtractor textExtractor,
        LitigationOrderDocumentQueue queue,
        LitigationOrderChunkingQueue chunkingQueue,
        IOptions<BprLitigationOptions> options,
        IWebHostEnvironment env,
        ILogger<LitigationOrderDocumentService> logger)
        : this(db, [new BprOrderSourceClient(client, options)], reservations, textExtractor, queue, chunkingQueue, options, env, logger)
    {
    }

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

        // An order the source lists without a link has nothing to download: it is Not Published (terminal) until a litigation refresh
        // re-admits it with a link. It used to be left Failed — "retry later" — yet never retried, so the litigation analysis, which waits
        // for every order to be terminal, waited out the whole 7-day retention window (Lifex test, 2026-10-13).
        OrderSourceLocator? locator = null;
        if (!string.IsNullOrWhiteSpace(document.Order.SourceLocator))
        {
            try { locator = JsonSerializer.Deserialize<OrderSourceLocator>(document.Order.SourceLocator); }
            catch { }
        }
        locator ??= new OrderSourceLocator(
            Source: document.Order.Source ?? "Bpr",
            PdfUrl: document.Order.PdfUrl,
            OrderType: document.Order.OrderType,
            OrderDate: document.Order.OrderDate);

        var hasLink = !string.IsNullOrWhiteSpace(locator.PdfUrl) ||
                      !string.IsNullOrWhiteSpace(locator.OrderUrl) ||
                      locator.FilingReferenceNumber.HasValue;

        if (document.Status == LitigationOrderDocumentStatus.Failed && !hasLink)
        {
            await MarkFailedWithoutLinkNotPublishedAsync(document.LitigationOrderDocumentId, ct);
            return;
        }

        var client = _sourceClients.FirstOrDefault(c => c.CanHandle(locator));
        if (client is null)
        {
            if (string.Equals(locator.Source, "Bpr", StringComparison.OrdinalIgnoreCase) && !_opts.IsConfigured) return;
            logger.LogWarning("No order source adapter found for order document {Id} with source {Source}.", orderDocumentId, locator.Source);
            return;
        }
        if (client is BprOrderSourceClient && !_opts.IsConfigured) return;

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
            if (!hasLink)
            {
                await MarkNotPublishedAsync(orderDocumentId, myLeaseToken, NoLinkReason, ct);
                return;
            }

            var limit = _opts.MaxOrderPdfBytes > 0 ? _opts.MaxOrderPdfBytes : 20 * 1024 * 1024;
            var download = await client.DownloadAsync(locator, limit, ct);
            if (download.NotPublished)
            {
                await MarkNotPublishedAsync(orderDocumentId, myLeaseToken, download.Error ?? "Order not published at source", ct);
                return;
            }
            if (!download.Ok)
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, download.Error ?? "Download failed", ct);
                return;
            }

            // An order served as an HTML page is kept as its plain text (.txt), never as HTML.
            var isText = download.Text is not null;
            var fileBytes = isText ? new System.Text.UTF8Encoding(false).GetBytes(download.Text!) : download.Bytes!;

            Directory.CreateDirectory(directory);
            // Keyed by this attempt's own lease token — never a fixed/shared path — so a stale straggler and
            // a takeover can never write the same file (see this type's own remarks).
            myPath = Path.Combine(directory, $"{orderDocumentId}-{myLeaseToken:N}{(isText ? ".txt" : ".pdf")}");
            var reservationOwnerId = $"{orderDocumentId}:{myLeaseToken:N}";

            var reservation = await reservations.TryReserveAsync(
                "LitigationOrderDocument", reservationOwnerId, directory, fileBytes.LongLength, TimeSpan.FromMinutes(LeaseMinutes), ct);
            if (!reservation.Success)
            {
                await MarkFailedOrExpiredAsync(orderDocumentId, myLeaseToken, retainedUntilUtc, attemptCount, $"Not enough disk space to retain this order: {reservation.Error}", ct);
                myPath = null; // nothing was ever written — no file for the finally block to clean up
                return;
            }

            try
            {
                await File.WriteAllBytesAsync(myPath, fileBytes, ct);
            }
            finally
            {
                await reservations.ReleaseReservationsAsync("LitigationOrderDocument", reservationOwnerId, CancellationToken.None);
            }

            var fileHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(fileBytes));
            var contentType = download.ContentType ?? (isText ? "text/plain; charset=utf-8" : "application/pdf");
            var downloadedUtc = DateTime.UtcNow;

            var contentHash = isText
                ? OrderContentHasher.ComputeHtmlTextHash(download.Text)
                : fileHash;

            // Extraction failure never undoes a successful download — the file is retained regardless (that
            // is the primary contract); a null TextExtractionStatus just means extraction was never
            // successfully attempted, distinct from CorruptPdf/PasswordProtected (attempted, and failed).
            string? extractedText = null;
            FilingDocumentProcessingStatus? textStatus = null;
            TextExtractionMethod? textMethod = null;
            DateTime? extractedUtc = null;
            if (download.IsUnsupportedFormat)
            {
                textStatus = FilingDocumentProcessingStatus.UnsupportedPdf;
                textMethod = TextExtractionMethod.None;
                extractedUtc = DateTime.UtcNow;
            }
            else if (isText)
            {
                extractedText = LitigationOrderText.AsExtractedText(download.Text!);
                textStatus = FilingDocumentProcessingStatus.TextExtracted;
                textMethod = TextExtractionMethod.Html;
                extractedUtc = DateTime.UtcNow;
            }
            else try
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

            // Version rule evaluation: check if content changed under the same identity
            var currentHash = document.ContentHash ?? document.Order.ContentHash;
            var currentVer = document.Version > 0 ? document.Version : (document.Order.Version > 0 ? document.Order.Version : 1);
            var versionDecision = OrderVersionRule.Evaluate(currentHash, currentVer, contentHash);

            if (versionDecision.Decision == OrderVersionDecisionType.Superseded)
            {
                // Archive prior document if it was already Downloaded with prior content
                if (document.ContentHash != null && !string.Equals(document.ContentHash, contentHash, StringComparison.OrdinalIgnoreCase))
                {
                    var archivedDoc = new LitigationOrderDocument
                    {
                        LitigationCaseOrderId = document.LitigationCaseOrderId,
                        Status = document.Status,
                        StoragePath = document.StoragePath,
                        FileSizeBytes = document.FileSizeBytes,
                        FileHash = document.FileHash,
                        ContentType = document.ContentType,
                        DownloadedUtc = document.DownloadedUtc,
                        ExtractedText = document.ExtractedText,
                        TextExtractionStatus = document.TextExtractionStatus,
                        TextExtractionMethod = document.TextExtractionMethod,
                        ExtractedUtc = document.ExtractedUtc,
                        ContentHash = document.ContentHash,
                        Version = document.Version,
                        IsSuperseded = true,
                        RetainedUntilUtc = document.RetainedUntilUtc,
                        CreatedUtc = document.CreatedUtc,
                    };
                    db.LitigationOrderDocuments.Add(archivedDoc);
                    await db.SaveChangesAsync(ct);
                }

                // Mark any other older documents and chunks for this order as superseded
                await db.LitigationOrderDocuments
                    .Where(d => d.LitigationCaseOrderId == document.LitigationCaseOrderId && d.LitigationOrderDocumentId != orderDocumentId)
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsSuperseded, true), ct);

                await db.LitigationOrderChunks
                    .Where(c => c.LitigationCaseOrderId == document.LitigationCaseOrderId)
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsSuperseded, true), ct);
            }

            // The publish itself: guarded by this attempt's own lease token still being current AND
            // unexpired — never a plain SaveChangesAsync on the tracked entity. 0 rows means a takeover (or
            // simple lease timeout) has already superseded this attempt; its file must never become the
            // recorded one.
            // The publish and the request's live-data version move in ONE transaction: the text is stored only together with the bump, so a stop between them cannot leave the
            // caches on the old version (a retry would find the text already stored and not bump again).
            await using var publication = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(ct) : null;
            var claimed = await LeaseGuarded(orderDocumentId, myLeaseToken).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, LitigationOrderDocumentStatus.Downloaded)
                .SetProperty(d => d.StoragePath, myPath)
                .SetProperty(d => d.FileSizeBytes, fileBytes.LongLength)
                .SetProperty(d => d.FileHash, fileHash)
                .SetProperty(d => d.ContentHash, contentHash)
                .SetProperty(d => d.Version, versionDecision.VersionNumber)
                .SetProperty(d => d.IsSuperseded, false)
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
                return;   // the transaction is rolled back on dispose; nothing was stored
            }

            // Sync version and content hash back to LitigationCaseOrder
            await db.LitigationCaseOrders
                .Where(o => o.LitigationCaseOrderId == document.LitigationCaseOrderId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(o => o.ContentHash, contentHash)
                    .SetProperty(o => o.Version, versionDecision.VersionNumber)
                    .SetProperty(o => o.IsSuperseded, false), ct);

            // Order text arriving changes what the request's litigation views and analyses read: it is a publication, so the request's live-data version moves in the same transaction.
            if (textStatus == FilingDocumentProcessingStatus.TextExtracted)
            {
                var bumpRequestId = await db.LitigationOrderDocuments.AsNoTracking().Where(d => d.LitigationOrderDocumentId == orderDocumentId)
                    .Select(d => (long?)d.Order!.Case!.RequestId).FirstOrDefaultAsync(ct);
                if (bumpRequestId is { } rid) await MCAROC_Analysis.Services.LitigationModule.Refresh.RequestLiveData.BumpAsync(db, rid, ct);
            }
            if (publication is not null) await publication.CommitAsync(ct);
            published = true;
            logger.LogInformation(
                "Litigation order document {Id} downloaded and extracted ({Bytes} bytes, {Method}).",
                orderDocumentId, fileBytes.LongLength, textMethod);

            // Check cross-source equivalence and collapse duplicates if applicable
            var isCollapsedLoser = await CheckAndCollapseEquivalenceAsync(document, contentHash, textStatus, extractedText, ct);

            // #244/LIT-04: only enqueue for chunking once text extraction actually succeeded and order is not a collapsed loser
            if (!isCollapsedLoser && textStatus == FilingDocumentProcessingStatus.TextExtracted)
                chunkingQueue.Enqueue(orderDocumentId);
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
    /// <summary>Terminal: the source has no order document behind this link. Never retried — only a refresh that
    /// brings a new link (see <c>LitigationCasePersistenceService</c>) puts it back to Pending.</summary>
    internal const string NoLinkReason = "The source report lists this order without a document link; nothing can be downloaded until a refresh brings one.";

    /// <summary>A row left Failed for want of a link (the old behaviour) becomes Not Published. Unleased, so conditional on still being a linkless
    /// failure: a refresh that meanwhile brought a link has already moved it back to Pending.</summary>
    private Task<int> MarkFailedWithoutLinkNotPublishedAsync(long documentId, CancellationToken ct) =>
        db.LitigationOrderDocuments.Where(d => d.LitigationOrderDocumentId == documentId && d.Status == LitigationOrderDocumentStatus.Failed
                && (d.Order!.PdfUrl == null || d.Order.PdfUrl == "")
                && (d.Order.SourceLocator == null || d.Order.SourceLocator == ""))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, LitigationOrderDocumentStatus.NotPublished)
                .SetProperty(d => d.FailureReason, NoLinkReason).SetProperty(d => d.NextAttemptUtc, (DateTime?)null), ct);

    private async Task MarkNotPublishedAsync(long documentId, Guid leaseToken, string reason, CancellationToken ct)
    {
        var claimed = await LeaseGuarded(documentId, leaseToken).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, LitigationOrderDocumentStatus.NotPublished)
            .SetProperty(d => d.FailureReason, reason)
            .SetProperty(d => d.NextAttemptUtc, (DateTime?)null), ct);
        if (claimed == 0)
            logger.LogWarning(
                "Litigation order document {Id} lost its lease before 'not published' could be recorded — leaving it to whichever attempt now owns it.",
                documentId);
    }

    private async Task MarkFailedOrExpiredAsync(
        long documentId, Guid leaseToken, DateTime retainedUntilUtc, int attemptCount, string reason, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var newStatus = retainedUntilUtc <= now ? LitigationOrderDocumentStatus.Expired : LitigationOrderDocumentStatus.Failed;

        // Failure time + backoff, persisted with the failure itself so recovery after a restart reschedules the same moment.
        var readyUtc = now + RetryDelay(attemptCount);
        var effectiveReadyUtc = readyUtc < retainedUntilUtc ? readyUtc : retainedUntilUtc;
        DateTime? nextAttemptUtc = newStatus == LitigationOrderDocumentStatus.Failed ? effectiveReadyUtc : null;

        var claimed = await LeaseGuarded(documentId, leaseToken).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Status, newStatus)
            .SetProperty(d => d.FailureReason, reason)
            .SetProperty(d => d.NextAttemptUtc, nextAttemptUtc), ct);
        if (claimed == 0)
        {
            logger.LogWarning(
                "Litigation order document {Id} lost its lease before its failure could be recorded — leaving it to whichever attempt now owns it.",
                documentId);
            return;
        }

        if (newStatus == LitigationOrderDocumentStatus.Failed)
            ScheduleRetry(documentId, effectiveReadyUtc, ct);
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

    private async Task<bool> CheckAndCollapseEquivalenceAsync(
        LitigationOrderDocument document,
        string contentHash,
        FilingDocumentProcessingStatus? textStatus,
        string? extractedText,
        CancellationToken ct)
    {
        if (document.Order?.Case is null) return false;
        var order = document.Order;
        var caseId = order.LitigationCaseId;
        var requestId = order.Case.RequestId;
        var isVerified = await IsCaseVerifiedAsync(requestId, caseId, ct);
        if (!isVerified) return false;

        var orderDate = CaseDates.ParseDate(order.OrderDate);
        if (!orderDate.HasValue) return false;

        var otherOrders = await db.LitigationCaseOrders.AsNoTracking()
            .Where(o => o.LitigationCaseId == caseId && o.LitigationCaseOrderId != order.LitigationCaseOrderId && o.CollapsedIntoOrderId == null && !o.IsSuperseded)
            .ToListAsync(ct);

        var hasExtracted = textStatus == FilingDocumentProcessingStatus.TextExtracted && !string.IsNullOrWhiteSpace(extractedText);
        var currentItem = new OrderComparisonItem(
            OrderId: order.LitigationCaseOrderId,
            Source: order.Source ?? "Bpr",
            OrderKey: order.OrderKey ?? "",
            CaseId: caseId,
            IsCaseVerified: isVerified,
            OrderDate: orderDate,
            ContentHash: contentHash,
            HasExtractedText: hasExtracted);

        var amILoser = false;
        foreach (var other in otherOrders)
        {
            var otherDate = CaseDates.ParseDate(other.OrderDate);
            if (otherDate != orderDate) continue;

            var otherDoc = await db.LitigationOrderDocuments.AsNoTracking()
                .Where(d => d.LitigationCaseOrderId == other.LitigationCaseOrderId && !d.IsSuperseded && d.Status == LitigationOrderDocumentStatus.Downloaded)
                .OrderByDescending(d => d.Version)
                .FirstOrDefaultAsync(ct);

            var otherHash = other.ContentHash ?? otherDoc?.ContentHash;
            var otherHasText = otherDoc?.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted && !string.IsNullOrWhiteSpace(otherDoc.ExtractedText);

            var otherItem = new OrderComparisonItem(
                OrderId: other.LitigationCaseOrderId,
                Source: other.Source ?? "Bpr",
                OrderKey: other.OrderKey ?? "",
                CaseId: caseId,
                IsCaseVerified: isVerified,
                OrderDate: otherDate,
                ContentHash: otherHash,
                HasExtractedText: otherHasText);

            var eq = OrderEquivalence.Evaluate(currentItem, otherItem);
            if (eq.IsEquivalent && eq.SurvivorOrderId.HasValue && eq.CollapsedOrderId.HasValue)
            {
                var survivorId = eq.SurvivorOrderId.Value;
                var collapsedId = eq.CollapsedOrderId.Value;

                await db.LitigationCaseOrders
                    .Where(o => o.LitigationCaseOrderId == collapsedId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(o => o.CollapsedIntoOrderId, survivorId)
                        .SetProperty(o => o.IsSuperseded, true), ct);

                await db.LitigationOrderDocuments
                    .Where(d => d.LitigationCaseOrderId == collapsedId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(d => d.IsSuperseded, true), ct);

                await db.LitigationOrderChunks
                    .Where(c => c.LitigationCaseOrderId == collapsedId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(c => c.IsSuperseded, true), ct);

                logger.LogInformation(
                    "Cross-source order collapse: Order {CollapsedId} collapsed into {SurvivorId} ({Reason})",
                    collapsedId, survivorId, eq.Reason);

                if (collapsedId == order.LitigationCaseOrderId)
                {
                    amILoser = true;
                    break;
                }
            }
        }

        return amILoser;
    }

    private async Task<bool> IsCaseVerifiedAsync(long requestId, long caseId, CancellationToken ct)
    {
        return await db.LitigationCases.AsNoTracking()
            .AnyAsync(c => c.LitigationCaseId == caseId && c.RequestId == requestId && c.MergedIntoCaseId == null, ct);
    }

    // ── Recovery ───────────────────────────────────────────────────────────────────────────────────

    private static TimeSpan RetryDelay(int attemptCount) =>
        TimeSpan.FromMinutes(Math.Min(180, 5 * Math.Pow(2, Math.Max(0, attemptCount - 1))));

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
    /// refresh. A failed row keeps its backoff across a restart (measured from its last claim, which minted
    /// <c>NextAttemptUtc</c>), so a restart never re-attempts every failure at once, and a failed row with no
    /// PdfUrl is not re-attempted at all.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var hasNonBpr = _sourceClients.Any(c => c is not BprOrderSourceClient);
        if (!_opts.IsConfigured && !hasNonBpr) return 0; // every attempt would fail; rows stay as-is until the client is configured

        var now = DateTime.UtcNow;
        // Rows left Failed for want of a link (before that became Not Published) would hold every litigation analysis back for up to 7 days.
        await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Failed
                && (d.Order!.PdfUrl == null || d.Order.PdfUrl == "")
                && (d.Order.SourceLocator == null || d.Order.SourceLocator == ""))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, LitigationOrderDocumentStatus.NotPublished)
                .SetProperty(d => d.FailureReason, NoLinkReason).SetProperty(d => d.NextAttemptUtc, (DateTime?)null), ct);
        var nonTerminal = await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Pending || d.Status == LitigationOrderDocumentStatus.InProgress ||
                (d.Status == LitigationOrderDocumentStatus.Failed && d.Order != null
                    && ((d.Order.PdfUrl != null && d.Order.PdfUrl != "") || (d.Order.SourceLocator != null && d.Order.SourceLocator != ""))))
            .Select(d => new { d.LitigationOrderDocumentId, d.Status, d.LeaseExpiresUtc, d.NextAttemptUtc, d.AttemptCount, d.RetainedUntilUtc })
            .ToListAsync(ct);

        foreach (var d in nonTerminal)
        {
            if (d.Status == LitigationOrderDocumentStatus.InProgress && d.LeaseExpiresUtc is { } expires && expires > now)
                ScheduleRetry(d.LitigationOrderDocumentId, expires, ct);
            else if (d.Status == LitigationOrderDocumentStatus.Failed)
            {
                // A row that failed before NextAttemptUtc existed has no recorded deadline: back it off from now
                // rather than retrying every legacy failure at once.
                var readyUtc = d.NextAttemptUtc ?? now + RetryDelay(d.AttemptCount);
                ScheduleRetry(d.LitigationOrderDocumentId, readyUtc < d.RetainedUntilUtc ? readyUtc : d.RetainedUntilUtc, ct);
            }
            else
                queue.Enqueue(d.LitigationOrderDocumentId);
        }

        return nonTerminal.Count;
    }
}
