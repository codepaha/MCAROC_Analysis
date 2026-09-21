using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Chat;
using Microsoft.Data.SqlTypes;
using Microsoft.EntityFrameworkCore;
// DocumentChunkingOrchestrator (Services.Chat) supplies ClassifyChunkingError/SanitizeAndCap — see the
// class doc comment below for why these are reused rather than duplicated.

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Chunks and embeds one <see cref="LitigationOrderDocument"/>'s extracted text at a time
/// (#244/LIT-04) — the litigation-specific counterpart to <c>Services.Chat.DocumentChunkingOrchestrator</c>,
/// mirroring its rollback-safe re-chunk shape (chunking/embedding — the parts that can fail — happen entirely
/// before touching the database; only once the full new chunk set is computed does one transaction delete the
/// document's existing chunks and insert the new set together, so a failure never destroys a previously
/// working index). Reuses <see cref="TextChunker"/> and <see cref="EmbeddingService"/> unmodified — both are
/// already fully generic — and <c>DocumentChunkingOrchestrator</c>'s own <c>ClassifyChunkingError</c>/
/// <c>SanitizeAndCap</c> static helpers rather than duplicating that regex-heavy PII/secret-redaction logic,
/// which has nothing McaFiling-specific about it.
///
/// Deliberately per-document, not per-batch like <c>DocumentChunkingOrchestrator</c>: litigation orders have
/// no batch concept (see <see cref="LitigationOrderChunkingQueue"/>'s own remarks), so there is no analogous
/// "list pending documents for this batch" fan-out step — the queue already carries exactly one order
/// document id per unit of work.
///
/// <b>Lease/fencing (PR #254 review round 1):</b> unlike <c>DocumentChunkingOrchestrator</c>'s plain status
/// flip, the claim here mints a fresh <see cref="LitigationOrderDocument.ChunkingLeaseToken"/> and
/// <see cref="LitigationOrderDocument.ChunkingLeaseExpiresUtc"/>, and every write that follows — the Chunked
/// completion and every Pending/Failed retry transition — is guarded by <see cref="ChunkingLeaseGuarded"/>,
/// mirroring <c>LitigationOrderDocumentService.LeaseGuarded</c> exactly. A first cut of this orchestrator
/// copied <c>DocumentChunkingOrchestrator</c>'s unfenced "claim is just a status flip, recovery resets any
/// InProgress row unconditionally" shape — safe only under a single-instance assumption that this litigation
/// pipeline does not get to make: a second application instance starting up while the first is still actively
/// (and slowly — a real Vertex AI call) embedding would reset that live row to Pending and let both instances
/// embed and publish the same document, with the stale one able to overwrite the newer chunk set purely by
/// finishing last. The lease closes that: a fenced-out attempt's completion/failure write matches 0 rows and
/// is discarded (its own delete+insert is rolled back before ever committing), and startup recovery
/// (<see cref="RecoverStaleWorkAsync"/>) only ever resets a row whose lease has demonstrably expired.
///
/// <b>Delayed-reclaim liveness (PR #254 review round 2):</b> the claim below admits not just
/// <see cref="ChunkingStatus.Pending"/> but also an <see cref="ChunkingStatus.InProgress"/> row whose lease has
/// itself already expired (or was never set) — a single atomic <c>ExecuteUpdateAsync</c>, so two concurrent
/// reclaim attempts against the same expired-lease row still only ever let one through. A first cut of this
/// fix only reset an expired-lease row to Pending from <see cref="RecoverStaleWorkAsync"/>'s IMMEDIATE branch;
/// its DELAYED branch (<see cref="ScheduleRetry"/>, for a row whose lease was still live at sweep time) just
/// re-enqueued the id once that lease was due to expire, without ever performing that reset itself — so if the
/// original worker had genuinely crashed mid-lease, the row stayed InProgress forever (the old claim query
/// only ever matched Pending), stuck until some later, unrelated app restart happened to sweep it again.
/// Widening the claim itself to self-heal an expired-lease InProgress row fixes every path that can ever reach
/// it — immediate, delayed, or any other future trigger — with one rule, rather than duplicating "is this row
/// actually reclaimable" logic in both the claim and the delayed recheck.
///
/// <b>Lease renewal during embedding (PR #254 review round 2):</b> <see cref="EmbeddingService.EmbedDocumentsAsync"/>
/// loops sequential batches internally, so a single call for a large document's full chunk set could
/// legitimately run past a fixed lease window purely due to volume, not a crash — losing the lease mid-flight
/// would discard real, paid-for work and restart the whole document from scratch, repeatedly, for a document
/// that is otherwise healthy. <see cref="ChunkOrderDocumentAsync"/> instead calls <c>EmbedDocumentsAsync</c>
/// itself in <see cref="EmbedRenewalBatchSize"/>-sized groups (matching <c>EmbeddingService</c>'s own internal
/// per-call batch size, so each group is at most one real Vertex round-trip) and renews
/// <see cref="LitigationOrderDocument.ChunkingLeaseExpiresUtc"/>, lease-token-guarded, after each one — a
/// still-working attempt's lease is extended in step with its actual progress, while an attempt that stops
/// making real progress (crashed, or already fenced out by a takeover) simply stops renewing and its lease
/// expires on schedule. A renewal that itself finds 0 rows means a takeover has already superseded this
/// attempt; embedding stops immediately rather than paying for further Vertex calls no one can ever publish.
///
/// <b>Guarded compare-and-swap in recovery (PR #254 review round 3):</b> <see cref="RecoverStaleWorkAsync"/>
/// reads a snapshot of every InProgress row, then — for those it judged expired/unleased — issues a bulk
/// <c>ExecuteUpdateAsync</c> resetting them to Pending. Round 2's version of that write checked only
/// <c>ChunkingStatus == InProgress</c>, which is not enough on its own: between the read and the write, another
/// worker can have already atomically reclaimed one of those SAME rows via <see cref="ChunkOrderDocumentAsync"/>'s
/// own widened claim, minting a fresh token and a fresh, unexpired lease while <c>ChunkingStatus</c> stays
/// InProgress throughout — a status-only WHERE clause would still match that freshly (and legitimately)
/// reclaimed row and reset it back to Pending, invalidating a live owner's in-flight claim and letting a
/// second worker duplicate the same embedding work. The write now also re-checks <c>ChunkingLeaseExpiresUtc</c>
/// in the same atomic UPDATE — SQL Server evaluates the WHERE clause against each row's CURRENT, live data at
/// the moment the UPDATE actually executes, not against this method's stale in-memory snapshot from a moment
/// earlier, so a reclaimed row (whose fresh expiry is always in the future) no longer matches and is left
/// completely untouched. A follow-up read then enqueues only the rows that guarded transition actually landed
/// on, rather than every originally-read id regardless of outcome.</summary>
public sealed class LitigationOrderChunkingOrchestrator(
    AppDbContext db, EmbeddingService embeddingService, LitigationOrderChunkingQueue queue,
    ILogger<LitigationOrderChunkingOrchestrator> logger)
{
    public const string ChunkingVersion = "1.0";
    private const int MaxChunkRetryCount = 3;

    // How long a lease survives with no renewal before being treated as abandoned — not "the whole operation's
    // budget" (see EmbedRenewalBatchSize/renewal below), just "how quickly do we notice a genuine crash."
    private const int ChunkingLeaseMinutes = 10;

    // Caps each of THIS orchestrator's own calls to EmbedDocumentsAsync to at most one real Vertex round-trip
    // (matches EmbeddingService's own internal MaxBatchSize), so a lease renewal after each call tracks actual
    // embedding progress rather than just elapsed wall-clock time on one giant call this orchestrator could
    // never renew inside of.
    private const int EmbedRenewalBatchSize = 32;

    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task ChunkOrderDocumentAsync(long orderDocumentId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var myLeaseToken = Guid.NewGuid();
        // Admits Pending (the common case) and also an InProgress row whose lease has already expired or was
        // never set — the exact scenario RecoverStaleWorkAsync's delayed path re-enqueues once a live lease it
        // saw at sweep time is later due to expire. One atomic UPDATE...WHERE means two concurrent reclaim
        // attempts against the same expired-lease row still only ever let one through — SQL Server serializes
        // the second one behind the first, and by the time it re-evaluates, the row already carries the
        // winner's fresh (unexpired) lease, so its WHERE clause no longer matches.
        var claimed = await db.LitigationOrderDocuments
            .Where(d => d.LitigationOrderDocumentId == orderDocumentId &&
                (d.ChunkingStatus == ChunkingStatus.Pending ||
                    (d.ChunkingStatus == ChunkingStatus.InProgress &&
                        (d.ChunkingLeaseExpiresUtc == null || d.ChunkingLeaseExpiresUtc <= now))))
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                .SetProperty(d => d.ChunkingLeaseToken, myLeaseToken)
                .SetProperty(d => d.ChunkingLeaseOwner, LeaseOwnerId)
                .SetProperty(d => d.ChunkingLeaseExpiresUtc, now.AddMinutes(ChunkingLeaseMinutes))
                .SetProperty(d => d.ChunkingLastAttemptUtc, now), ct);
        if (claimed == 0)
            return; // already claimed/chunked by another still-live attempt

        var document = await db.LitigationOrderDocuments.Include(d => d.Order).ThenInclude(o => o!.Case)
            .FirstAsync(d => d.LitigationOrderDocumentId == orderDocumentId, ct);

        try
        {
            if (document.Order?.Case is null)
                throw new InvalidOperationException(
                    $"Litigation order document {orderDocumentId} has no reachable LitigationCaseOrder/LitigationCase.");
            if (string.IsNullOrWhiteSpace(document.ExtractedText))
                throw new InvalidOperationException(
                    $"Extracted text not found for litigation order document {orderDocumentId} — nothing to chunk.");

            var textChunks = TextChunker.Chunk(document.ExtractedText, ChatIndexingOptions.Default);
            var embeddings = new List<float[]>(textChunks.Count);
            for (var offset = 0; offset < textChunks.Count; offset += EmbedRenewalBatchSize)
            {
                var batchTexts = textChunks.Skip(offset).Take(EmbedRenewalBatchSize).Select(c => c.Text).ToList();
                embeddings.AddRange(await embeddingService.EmbedDocumentsAsync(batchTexts, ct));

                var renewed = await ChunkingLeaseGuarded(orderDocumentId, myLeaseToken).ExecuteUpdateAsync(s =>
                    s.SetProperty(d => d.ChunkingLeaseExpiresUtc, DateTime.UtcNow.AddMinutes(ChunkingLeaseMinutes)), ct);
                if (renewed == 0)
                {
                    logger.LogWarning(
                        "Litigation order document {Id} lost its chunking lease mid-embed — a takeover already owns it, stopping further embedding calls.",
                        orderDocumentId);
                    return; // fenced out mid-flight; no further writes, no re-enqueue — a takeover owns this document now
                }
            }
            // Positional pairing below; a mismatch means an embedding call dropped/duplicated a vector — fail
            // this document (retry, then Failed) rather than persist a misaligned or truncated index.
            if (embeddings.Count != textChunks.Count)
                throw new InvalidOperationException(
                    $"Embedding count {embeddings.Count} does not match chunk count {textChunks.Count} " +
                    $"for litigation order document {orderDocumentId}.");

            var order = document.Order;
            var litigationCase = order.Case!;
            var newChunks = new List<LitigationOrderChunk>(textChunks.Count);
            for (var i = 0; i < textChunks.Count; i++)
            {
                newChunks.Add(new LitigationOrderChunk
                {
                    RequestId = litigationCase.RequestId,
                    LitigationOrderDocumentId = document.LitigationOrderDocumentId,
                    LitigationCaseOrderId = order.LitigationCaseOrderId,
                    LitigationCaseId = litigationCase.LitigationCaseId,
                    CaseNumber = litigationCase.CaseNumber,
                    Cnr = litigationCase.Cnr,
                    Court = litigationCase.Court,
                    OrderType = order.OrderType,
                    OrderDate = order.OrderDate,
                    ChunkIndex = i,
                    PageNumber = textChunks[i].PageNumber,
                    ChunkText = textChunks[i].Text,
                    Embedding = new SqlVector<float>(embeddings[i]),
                    EmbeddingModel = EmbeddingService.ModelId,
                    EmbeddingDimensions = EmbeddingService.Dimensions,
                    ChunkingVersion = ChunkingVersion,
                    CreatedDate = DateTime.UtcNow
                });
            }

            // The delete+insert and the lease-guarded completion write share one transaction: if the guarded
            // update matches 0 rows (this attempt was fenced out — superseded by a takeover, or its own lease
            // simply expired mid-embed), the whole transaction rolls back, so a stale attempt's chunk set
            // never lands even transiently. Only a successful, still-authoritative completion commits.
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.LitigationOrderChunks.Where(c => c.LitigationOrderDocumentId == orderDocumentId).ExecuteDeleteAsync(ct);
            db.LitigationOrderChunks.AddRange(newChunks);
            await db.SaveChangesAsync(ct);
            var published = await ChunkingLeaseGuarded(orderDocumentId, myLeaseToken).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.Chunked)
                .SetProperty(d => d.ChunkingLastError, (string?)null)
                .SetProperty(d => d.ChunkingErrorCategory, (string?)null)
                .SetProperty(d => d.ChunkingFailedUtc, (DateTime?)null), ct);

            if (published == 0)
            {
                await transaction.RollbackAsync(ct);
                logger.LogWarning(
                    "Litigation order document {Id} lost its chunking lease before its chunk set could be published — discarding this attempt's results.",
                    orderDocumentId);
                return;
            }

            await transaction.CommitAsync(ct);
            logger.LogInformation("Litigation order document {Id} chunked: {Count} chunk(s).", orderDocumentId, textChunks.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chunking failed for litigation order document {Id}", orderDocumentId);
            var category = DocumentChunkingOrchestrator.ClassifyChunkingError(ex);
            var sanitizedMsg = DocumentChunkingOrchestrator.SanitizeAndCap(ex.Message, 500);
            var retryCount = document.ChunkRetryCount + 1;
            var isTerminal = retryCount >= MaxChunkRetryCount;
            var nextStatus = isTerminal ? ChunkingStatus.Failed : ChunkingStatus.Pending;

            var recorded = await ChunkingLeaseGuarded(orderDocumentId, myLeaseToken).ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, nextStatus)
                .SetProperty(d => d.ChunkRetryCount, retryCount)
                .SetProperty(d => d.ChunkingLastError, sanitizedMsg)
                .SetProperty(d => d.ChunkingErrorCategory, category)
                .SetProperty(d => d.ChunkingFailedUtc, isTerminal ? (DateTime?)DateTime.UtcNow : null), ct);

            if (recorded == 0)
            {
                // Fenced out — a takeover already owns this document's retry accounting; recording our own
                // failure here would either clobber a newer attempt's state or resurrect a row a takeover has
                // already moved past. Leave it entirely alone, and never re-enqueue on its behalf.
                logger.LogWarning(
                    "Litigation order document {Id} lost its chunking lease before its failure could be recorded — leaving it to whichever attempt now owns it.",
                    orderDocumentId);
                return;
            }

            if (!isTerminal)
                queue.Enqueue(orderDocumentId);
        }
    }

    /// <summary>The query every write after a claim must go through — requires this exact attempt's chunking
    /// lease token and an unexpired lease, not just any tracked-entity SaveChanges or a plain id filter. An
    /// ExecuteUpdateAsync against this query returning 0 means the row no longer matches (fenced out); callers
    /// must treat that as "stop touching this document," never as an ordinary failure to retry. Mirrors
    /// <c>LitigationOrderDocumentService.LeaseGuarded</c> exactly.</summary>
    private IQueryable<LitigationOrderDocument> ChunkingLeaseGuarded(long documentId, Guid leaseToken)
    {
        var now = DateTime.UtcNow;
        return db.LitigationOrderDocuments.Where(d =>
            d.LitigationOrderDocumentId == documentId && d.ChunkingLeaseToken == leaseToken &&
            d.ChunkingLeaseExpiresUtc != null && d.ChunkingLeaseExpiresUtc > now);
    }

    /// <summary>Startup recovery may only reclaim work whose chunking lease has demonstrably expired — a row
    /// still <see cref="ChunkingStatus.InProgress"/> with a live, unexpired lease is left completely alone and
    /// instead scheduled for a one-time re-check right when that lease is due to expire via <see cref="ScheduleRetry"/>
    /// (mirrors <c>LitigationOrderDocumentService.RecoverStaleWorkAsync</c>'s own InProgress/live-lease handling
    /// exactly). Resetting on sight — this orchestrator's original shape, matching
    /// <c>DocumentChunkingOrchestrator</c>'s own unfenced recovery — is safe only under a single-instance
    /// assumption; a second instance starting up while the first is still actively (and slowly) embedding
    /// would otherwise reset that live row and let both instances embed and publish the same document (PR
    /// #254 review round 1). Separately, every Downloaded-and-text-extracted document still Pending chunking
    /// is swept and re-enqueued regardless of its InProgress history — closes the gap where a document reaches
    /// ChunkingStatus.Pending (its default) without anything ever having enqueued it, e.g. after a migration
    /// backfill or a crash between a successful download publish and the chunking enqueue call.
    ///
    /// The explicit reset here for an already-expired/unleased row is an optimization, not the only path that
    /// can ever make such a row claimable again — <see cref="ChunkOrderDocumentAsync"/>'s own claim query (PR
    /// #254 review round 2) independently admits an expired-lease InProgress row too, so <see cref="ScheduleRetry"/>'s
    /// delayed re-enqueue for a row that was live at sweep time but has since genuinely expired (its owning
    /// process crashed and never renewed) succeeds without this method ever running again.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inProgress = await db.LitigationOrderDocuments
            .Where(d => d.ChunkingStatus == ChunkingStatus.InProgress)
            .Select(d => new { d.LitigationOrderDocumentId, d.ChunkingLeaseExpiresUtc })
            .ToListAsync(ct);

        var expiredOrUnleasedIds = new List<long>();
        foreach (var d in inProgress)
        {
            if (d.ChunkingLeaseExpiresUtc is { } expires && expires > now)
                ScheduleRetry(d.LitigationOrderDocumentId, expires, ct); // live lease — leave it to its own owner
            else
                expiredOrUnleasedIds.Add(d.LitigationOrderDocumentId); // no lease, or genuinely expired — abandoned
        }

        var actuallyResetIds = new List<long>();
        if (expiredOrUnleasedIds.Count > 0)
        {
            // Guarded compare-and-swap (PR #254 review round 3): re-checking only ChunkingStatus == InProgress
            // here is not enough — between the read above and this write, another worker can have already
            // atomically reclaimed one of these SAME rows via ChunkOrderDocumentAsync's own widened claim (PR
            // #254 review round 2), minting a fresh token and a fresh, unexpired lease while the row's
            // ChunkingStatus stays InProgress throughout. A WHERE clause that only checks ChunkingStatus would
            // still match that freshly (and legitimately) reclaimed row and reset it back to Pending —
            // invalidating a live owner's in-flight claim and letting a second worker duplicate the same
            // embedding work. Re-checking ChunkingLeaseExpiresUtc in the SAME atomic UPDATE closes this: SQL
            // Server evaluates the WHERE clause against each row's CURRENT, live data at the moment the UPDATE
            // actually executes, not against this method's stale in-memory snapshot — a reclaimed row's fresh
            // expiry is always in the future, so it no longer matches and is left completely untouched.
            await db.LitigationOrderDocuments
                .Where(d => expiredOrUnleasedIds.Contains(d.LitigationOrderDocumentId) && d.ChunkingStatus == ChunkingStatus.InProgress
                    && (d.ChunkingLeaseExpiresUtc == null || d.ChunkingLeaseExpiresUtc <= now))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.Pending), ct);

            // Enqueue only the rows the guarded transition above actually landed on — a row skipped by that
            // guard (because it was reclaimed in between) is already owned by whoever reclaimed it and needs
            // no help from this sweep; enqueueing its id anyway would just be a wasted, no-op claim attempt at
            // best, and at worst papers over a bug if the guard above is ever weakened later.
            actuallyResetIds = await db.LitigationOrderDocuments
                .Where(d => expiredOrUnleasedIds.Contains(d.LitigationOrderDocumentId) && d.ChunkingStatus == ChunkingStatus.Pending)
                .Select(d => d.LitigationOrderDocumentId)
                .ToListAsync(ct);
            foreach (var id in actuallyResetIds)
                queue.Enqueue(id);
        }

        var pendingEligibleIds = await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Downloaded
                && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.LitigationOrderDocumentId)
            .ToListAsync(ct);

        foreach (var id in pendingEligibleIds.Except(actuallyResetIds))
            queue.Enqueue(id);

        return inProgress.Count + pendingEligibleIds.Except(actuallyResetIds).Count();
    }

    /// <summary>One-time delayed re-enqueue for a document whose chunking lease is still live at recovery
    /// time — fires once that lease is actually due to expire, so a genuinely abandoned attempt (the owning
    /// process crashed and never renewed/completed it) still gets picked back up without this recovery sweep
    /// having to guess at a live lease's remaining owner. Only ever enqueues the id — the actual reclaim
    /// (InProgress → InProgress-under-a-new-token) happens inside <see cref="ChunkOrderDocumentAsync"/>'s own
    /// claim query when the worker dequeues it (PR #254 review round 2: a first cut of this method assumed a
    /// plain enqueue was enough, but the claim at the time only ever admitted Pending, so a row that was
    /// genuinely still InProgress when the delay elapsed stayed stuck forever — closed by widening the claim
    /// itself, not by duplicating a reclaim step here). Mirrors <c>LitigationOrderDocumentService.ScheduleRetry</c>'s
    /// shutdown handling: if the app stops before the delay elapses, the next startup's recovery sweep
    /// re-evaluates the row from scratch.</summary>
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
                // App shutting down — the row stays as-is; the next startup's recovery sweep re-evaluates it.
            }
        }, ct);
    }
}
