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
/// (<see cref="RecoverStaleWorkAsync"/>) only ever resets a row whose lease has demonstrably expired.</summary>
public sealed class LitigationOrderChunkingOrchestrator(
    AppDbContext db, EmbeddingService embeddingService, LitigationOrderChunkingQueue queue,
    ILogger<LitigationOrderChunkingOrchestrator> logger)
{
    public const string ChunkingVersion = "1.0";
    private const int MaxChunkRetryCount = 3;

    // One HTTP-ish call to Vertex AI (batched, up to MaxBatchSize=32 texts per round-trip) plus DB work —
    // generous but not job-length, same reasoning as LitigationOrderDocumentService.LeaseMinutes.
    private const int ChunkingLeaseMinutes = 10;
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    public async Task ChunkOrderDocumentAsync(long orderDocumentId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var myLeaseToken = Guid.NewGuid();
        var claimed = await db.LitigationOrderDocuments
            .Where(d => d.LitigationOrderDocumentId == orderDocumentId && d.ChunkingStatus == ChunkingStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.ChunkingStatus, ChunkingStatus.InProgress)
                .SetProperty(d => d.ChunkingLeaseToken, myLeaseToken)
                .SetProperty(d => d.ChunkingLeaseOwner, LeaseOwnerId)
                .SetProperty(d => d.ChunkingLeaseExpiresUtc, now.AddMinutes(ChunkingLeaseMinutes))
                .SetProperty(d => d.ChunkingLastAttemptUtc, now), ct);
        if (claimed == 0)
            return; // already claimed/chunked by another worker or a previous run

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
            var embeddings = textChunks.Count > 0
                ? await embeddingService.EmbedDocumentsAsync(textChunks.Select(c => c.Text).ToList(), ct)
                : [];
            // Positional pairing below; a mismatch means the embedding call dropped/duplicated a vector —
            // fail this document (retry, then Failed) rather than persist a misaligned or truncated index.
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
    /// instead scheduled for a one-time re-check right when that lease is due to expire (mirrors
    /// <c>LitigationOrderDocumentService.RecoverStaleWorkAsync</c>'s own InProgress/live-lease handling
    /// exactly). Resetting on sight — this orchestrator's original shape, matching
    /// <c>DocumentChunkingOrchestrator</c>'s own unfenced recovery — is safe only under a single-instance
    /// assumption; a second instance starting up while the first is still actively (and slowly) embedding
    /// would otherwise reset that live row and let both instances embed and publish the same document (PR
    /// #254 review round 1). Separately, every Downloaded-and-text-extracted document still Pending chunking
    /// is swept and re-enqueued regardless of its InProgress history — closes the gap where a document reaches
    /// ChunkingStatus.Pending (its default) without anything ever having enqueued it, e.g. after a migration
    /// backfill or a crash between a successful download publish and the chunking enqueue call.</summary>
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

        if (expiredOrUnleasedIds.Count > 0)
        {
            // Re-checks ChunkingStatus == InProgress in the WHERE clause so a row that legitimately completed
            // or failed between the read above and this write is never clobbered back to Pending.
            await db.LitigationOrderDocuments
                .Where(d => expiredOrUnleasedIds.Contains(d.LitigationOrderDocumentId) && d.ChunkingStatus == ChunkingStatus.InProgress)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ChunkingStatus, ChunkingStatus.Pending), ct);
            foreach (var id in expiredOrUnleasedIds)
                queue.Enqueue(id);
        }

        var pendingEligibleIds = await db.LitigationOrderDocuments
            .Where(d => d.Status == LitigationOrderDocumentStatus.Downloaded
                && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted
                && d.ChunkingStatus == ChunkingStatus.Pending)
            .Select(d => d.LitigationOrderDocumentId)
            .ToListAsync(ct);

        foreach (var id in pendingEligibleIds.Except(expiredOrUnleasedIds))
            queue.Enqueue(id);

        return inProgress.Count + pendingEligibleIds.Except(expiredOrUnleasedIds).Count();
    }

    /// <summary>One-time delayed re-enqueue for a document whose chunking lease is still live at recovery
    /// time — fires once that lease is actually due to expire, so a genuinely abandoned attempt (the owning
    /// process crashed and never renewed/completed it) still gets picked back up without this recovery sweep
    /// having to guess at a live lease's remaining owner. Mirrors <c>LitigationOrderDocumentService.ScheduleRetry</c>
    /// exactly, including its shutdown handling: if the app stops before the delay elapses, the next startup's
    /// recovery sweep re-evaluates the row from scratch.</summary>
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
