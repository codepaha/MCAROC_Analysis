using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

public record FinalizeResult(bool Success, string? Error, int StatusCode, long? DocumentId, long? BatchId);

public class FinalizationRecoveryService(
    AppDbContext db,
    IOptions<LargeArchiveUploadOptions> options,
    IStorageReservationManager reservationManager,
    IOperationalSlotLeaseService slotLeaseService,
    FilingProcessingQueue filingQueue,
    ILogger<FinalizationRecoveryService> logger)
{
    private readonly LargeArchiveUploadOptions _opts = options.Value;

    public async Task<FinalizeResult> CompleteSessionAsync(Guid sessionId, string clientToken, CancellationToken ct = default)
    {
        var session = await db.LargeArchiveUploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
        if (session is null)
            return new FinalizeResult(false, "Upload session not found.", 404, null, null);

        if (!ChunkStreamingService.VerifyToken(session.HashedCapabilityToken, clientToken))
            return new FinalizeResult(false, "Invalid capability token.", 403, null, null);

        if (session.Status == LargeArchiveUploadSessionStatus.Completed)
            return new FinalizeResult(true, null, 200, session.CreatedDocumentId, session.CreatedBatchId);

        if (session.Status != LargeArchiveUploadSessionStatus.Uploading
            && session.Status != LargeArchiveUploadSessionStatus.Verifying
            && session.Status != LargeArchiveUploadSessionStatus.ArchiveMoved
            && session.Status != LargeArchiveUploadSessionStatus.DocumentCommitted
            && session.Status != LargeArchiveUploadSessionStatus.BatchCreated
            && session.Status != LargeArchiveUploadSessionStatus.Queued)
            return new FinalizeResult(false, $"Session is in '{session.Status}' state; cannot finalize.", 400, null, null);

        if (session.NextExpectedOffset < session.TotalExpectedSizeBytes)
            return new FinalizeResult(false, $"Upload incomplete. Received {session.NextExpectedOffset} of {session.TotalExpectedSizeBytes} bytes.", 400, null, null);

        // 1. Atomically claim finalization attempt with fresh FinalizationAttemptId
        var attemptId = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMinutes(5);
        var leaseExpires = DateTime.UtcNow.Add(leaseDuration);

        var targetStatus = session.Status == LargeArchiveUploadSessionStatus.Uploading
            ? LargeArchiveUploadSessionStatus.Verifying
            : session.Status;

        var claimed = await db.LargeArchiveUploadSessions
            .Where(s => s.SessionId == sessionId
                && (s.Status == LargeArchiveUploadSessionStatus.Uploading
                    || ((s.Status == LargeArchiveUploadSessionStatus.Verifying
                         || s.Status == LargeArchiveUploadSessionStatus.ArchiveMoved
                         || s.Status == LargeArchiveUploadSessionStatus.DocumentCommitted
                         || s.Status == LargeArchiveUploadSessionStatus.BatchCreated
                         || s.Status == LargeArchiveUploadSessionStatus.Queued)
                        && (s.FinalizationAttemptId == null || s.ActiveFinalizationExpiresUtc < DateTime.UtcNow))))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.Status, targetStatus)
                .SetProperty(s => s.FinalizationAttemptId, attemptId)
                .SetProperty(s => s.ActiveFinalizationExpiresUtc, leaseExpires)
                .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

        if (claimed == 0)
        {
            var current = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId, ct);
            if (current.Status == LargeArchiveUploadSessionStatus.Completed)
                return new FinalizeResult(true, null, 200, current.CreatedDocumentId, current.CreatedBatchId);

            return new FinalizeResult(false, "Finalization is already in progress by another worker.", 409, null, null);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        if (session.Status == LargeArchiveUploadSessionStatus.Uploading || session.Status == LargeArchiveUploadSessionStatus.Verifying)
        {
            // 2. Start background heartbeat renewal loop during hashing
            using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(60));
            var heartbeatTask = Task.Run(async () =>
            {
                try
                {
                    while (await heartbeatTimer.WaitForNextTickAsync(cts.Token))
                    {
                        var renewed = await db.LargeArchiveUploadSessions
                            .Where(s => s.SessionId == sessionId
                                && s.FinalizationAttemptId == attemptId
                                && s.Status == LargeArchiveUploadSessionStatus.Verifying)
                            .ExecuteUpdateAsync(u => u
                                .SetProperty(s => s.ActiveFinalizationExpiresUtc, DateTime.UtcNow.Add(leaseDuration))
                                .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), cts.Token);

                        if (renewed == 0)
                        {
                            logger.LogError("Finalization lease renewal failed for session {SessionId}; cancelling finalization.", sessionId);
                            cts.Cancel();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) { /* expected on finish */ }
            }, cts.Token);

            try
            {
                // 3. Compute full sequential SHA-256 and validate outer archive safety
                string fullHash;
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                await using (var fs = new FileStream(session.StagingFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan))
                {
                    var buffer = new byte[64 * 1024];
                    int r;
                    while ((r = await fs.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                    {
                        sha.AppendData(buffer, 0, r);
                    }
                    fullHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
                }

                if (!string.Equals(fullHash, session.ExpectedFullSha256.Trim().ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Full-file SHA-256 mismatch. Declared: {session.ExpectedFullSha256}, Computed: {fullHash}.");
                }

                var safetyLimits = ArchiveSafetyLimits.FromOptions(_opts);
                var safetyCheck = ArchiveSafetyValidator.ValidateOuterArchive(session.StagingFilePath, safetyLimits);
                if (!safetyCheck.IsValid)
                {
                    throw new InvalidOperationException($"Outer archive safety check failed: {safetyCheck.Error}");
                }

                // Stop heartbeat task before starting move transaction
                cts.Cancel();
                try { await heartbeatTask; } catch { /* ignore cancelled */ }

                // 4. Dedicated move transaction holding sp_getapplock
                await ExecuteFencedMoveAsync(session, attemptId, ct);
                session.Status = LargeArchiveUploadSessionStatus.ArchiveMoved;
            }
            catch (Exception ex)
            {
                cts.Cancel();
                try { await heartbeatTask; } catch { /* ignore */ }

                logger.LogError(ex, "Finalization failed for session {SessionId}", sessionId);

                // Storage admission guarantee: check whether archive was already moved to destination volume
                var moved = File.Exists(session.DestinationStoragePath)
                    || await db.LargeArchiveUploadSessions.AnyAsync(s => s.SessionId == sessionId
                        && (s.Status == LargeArchiveUploadSessionStatus.ArchiveMoved
                            || s.Status == LargeArchiveUploadSessionStatus.DocumentCommitted
                            || s.Status == LargeArchiveUploadSessionStatus.BatchCreated
                            || s.Status == LargeArchiveUploadSessionStatus.Queued
                            || s.Status == LargeArchiveUploadSessionStatus.Completed), CancellationToken.None);

                if (moved)
                {
                    // Archive already occupies destination storage. Do NOT release the reservation and do NOT mark Failed.
                    // Expire the lease attempt so background recovery worker can reconcile intermediate state safely.
                    await db.LargeArchiveUploadSessions
                        .Where(s => s.SessionId == sessionId && s.FinalizationAttemptId == attemptId)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(s => s.FailureReason, $"Interrupted during post-move finalization: {ex.Message}")
                            .SetProperty(s => s.ActiveFinalizationExpiresUtc, DateTime.UtcNow.AddSeconds(-1)), CancellationToken.None);
                }
                else
                {
                    // Move has not occurred. Pre-move failure. Mark Failed and release reservation & upload slot lease.
                    await db.LargeArchiveUploadSessions
                        .Where(s => s.SessionId == sessionId && s.FinalizationAttemptId == attemptId)
                        .ExecuteUpdateAsync(u => u
                            .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.Failed)
                            .SetProperty(s => s.FailureReason, ex.Message)
                            .SetProperty(s => s.ActiveFinalizationExpiresUtc, (DateTime?)null)
                            .SetProperty(s => s.FinalizationAttemptId, (Guid?)null), CancellationToken.None);

                    await reservationManager.ReleaseReservationsAsync("UploadSession", sessionId.ToString(), CancellationToken.None);
                    await slotLeaseService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), CancellationToken.None);
                }

                return new FinalizeResult(false, ex.Message, 500, null, null);
            }
        }

        try
        {
            // 5. Commit RequestDocument and McaFilingBatch entities with CAS
            var (docId, batchId) = await CommitDocumentAndBatchAsync(session, attemptId, ct);

            return new FinalizeResult(true, null, 200, docId, batchId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Finalization commit failed for session {SessionId}", sessionId);

            // Storage admission guarantee: Archive already occupies destination storage.
            // Do NOT release reservation; expire lease attempt so recovery worker reconciles intermediate state.
            await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == sessionId && s.FinalizationAttemptId == attemptId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.FailureReason, $"Interrupted during post-move commit: {ex.Message}")
                    .SetProperty(s => s.ActiveFinalizationExpiresUtc, DateTime.UtcNow.AddSeconds(-1)), CancellationToken.None);

            return new FinalizeResult(false, ex.Message, 500, null, null);
        }
    }

    private async Task ExecuteFencedMoveAsync(LargeArchiveUploadSession session, Guid attemptId, CancellationToken ct)
    {
        await using var moveTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);
        try
        {
            // Acquire SQL Server sp_getapplock (trimmed whitespace)
            var lockResource = $"FinalizationMove_{session.SessionId:N}";
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                DECLARE @res INT;
                EXEC @res = sp_getapplock
                    @Resource = {lockResource},
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = 10000;
                IF (@res < 0) THROW 50001, 'Lock acquisition timeout in sp_getapplock', 1;", ct);

            // Verify fence inside lock
            var fenceValid = await db.LargeArchiveUploadSessions
                .AnyAsync(s => s.SessionId == session.SessionId
                    && s.FinalizationAttemptId == attemptId
                    && s.Status == LargeArchiveUploadSessionStatus.Verifying
                    && s.ActiveFinalizationExpiresUtc > DateTime.UtcNow, ct);

            if (!fenceValid)
            {
                throw new InvalidOperationException("Finalization lease fence expired or was revoked before acquiring move lock.");
            }

            var destDir = Path.GetDirectoryName(session.DestinationStoragePath)!;
            Directory.CreateDirectory(destDir);

            // Atomic file move
            if (File.Exists(session.StagingFilePath))
            {
                File.Move(session.StagingFilePath, session.DestinationStoragePath, overwrite: false);
            }
            else if (!File.Exists(session.DestinationStoragePath))
            {
                throw new FileNotFoundException("Neither staging file nor destination file was found during finalization move.");
            }

            // CAS: Update session status to ArchiveMoved under the lock
            var movedCount = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == session.SessionId && s.FinalizationAttemptId == attemptId && s.Status == LargeArchiveUploadSessionStatus.Verifying)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.ArchiveMoved)
                    .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

            if (movedCount == 0)
            {
                throw new InvalidOperationException($"CAS transition to ArchiveMoved failed for session {session.SessionId}.");
            }

            await moveTx.CommitAsync(ct);
        }
        catch (Exception)
        {
            await moveTx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<(long docId, long batchId)> CommitDocumentAndBatchAsync(LargeArchiveUploadSession session, Guid attemptId, CancellationToken ct)
    {
        // 1. Commit Document
        var existingDoc = await db.RequestDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.UploadSessionId == session.SessionId, ct);
        if (existingDoc is null)
        {
            var fileInfo = new FileInfo(session.DestinationStoragePath);
            existingDoc = new RequestDocument
            {
                RequestId = session.RequestId,
                DocumentType = DocumentType.McaFilingsArchive,
                OriginalFileName = session.OriginalFileName,
                StoredFileName = Path.GetFileName(session.DestinationStoragePath),
                StoragePath = session.DestinationStoragePath,
                FileSize = fileInfo.Exists ? fileInfo.Length : session.TotalExpectedSizeBytes,
                FileHash = session.ExpectedFullSha256,
                UploadStatus = DocumentUploadStatus.Uploaded,
                UploadedDate = DateTime.UtcNow,
                IsActiveSource = false,
                UploadSessionId = session.SessionId
            };
            try
            {
                db.RequestDocuments.Add(existingDoc);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                existingDoc = await db.RequestDocuments.AsNoTracking().FirstAsync(d => d.UploadSessionId == session.SessionId, ct);
            }
        }

        if (session.Status < LargeArchiveUploadSessionStatus.DocumentCommitted)
        {
            // CAS: ArchiveMoved -> DocumentCommitted
            var docCommitted = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == session.SessionId
                    && s.FinalizationAttemptId == attemptId
                    && s.Status == LargeArchiveUploadSessionStatus.ArchiveMoved)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.DocumentCommitted)
                    .SetProperty(s => s.CreatedDocumentId, existingDoc.DocumentId)
                    .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

            if (docCommitted == 0)
            {
                throw new InvalidOperationException($"CAS transition to DocumentCommitted failed for session {session.SessionId}. Attempt may have expired or been superseded.");
            }
            session.Status = LargeArchiveUploadSessionStatus.DocumentCommitted;
        }

        // 2. Commit Batch
        var existingBatch = await db.McaFilingBatches.AsNoTracking().FirstOrDefaultAsync(b => b.UploadSessionId == session.SessionId, ct);
        if (existingBatch is null)
        {
            existingBatch = new McaFilingBatch
            {
                RequestId = session.RequestId,
                SourceDocumentId = existingDoc.DocumentId,
                Status = FilingBatchStatus.Uploaded,
                StartedDate = DateTime.UtcNow,
                UploadSessionId = session.SessionId
            };
            try
            {
                db.McaFilingBatches.Add(existingBatch);
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                existingBatch = await db.McaFilingBatches.AsNoTracking().FirstAsync(b => b.UploadSessionId == session.SessionId, ct);
            }
        }

        if (session.Status < LargeArchiveUploadSessionStatus.BatchCreated)
        {
            // Transition storage reservation to batch
            await reservationManager.TransitionReservationToBatchAsync(session.SessionId, existingBatch.BatchId, ct);

            // CAS: DocumentCommitted -> BatchCreated
            var batchCommitted = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == session.SessionId
                    && s.FinalizationAttemptId == attemptId
                    && s.Status == LargeArchiveUploadSessionStatus.DocumentCommitted)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.BatchCreated)
                    .SetProperty(s => s.CreatedBatchId, existingBatch.BatchId)
                    .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

            if (batchCommitted == 0)
            {
                throw new InvalidOperationException($"CAS transition to BatchCreated failed for session {session.SessionId}. Attempt may have expired or been superseded.");
            }
            session.Status = LargeArchiveUploadSessionStatus.BatchCreated;
        }

        // 3. Enqueue to background processing
        if (session.Status < LargeArchiveUploadSessionStatus.Queued)
        {
            filingQueue.Enqueue(new UnpackBatchWorkItem(existingBatch.BatchId));

            // CAS: BatchCreated -> Queued
            var queued = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == session.SessionId
                    && s.FinalizationAttemptId == attemptId
                    && s.Status == LargeArchiveUploadSessionStatus.BatchCreated)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.Queued)
                    .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

            if (queued == 0)
            {
                throw new InvalidOperationException($"CAS transition to Queued failed for session {session.SessionId}. Attempt may have expired or been superseded.");
            }
            session.Status = LargeArchiveUploadSessionStatus.Queued;
        }

        // 4. Mark session completed
        if (session.Status < LargeArchiveUploadSessionStatus.Completed)
        {
            var completed = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == session.SessionId
                    && s.FinalizationAttemptId == attemptId
                    && s.Status == LargeArchiveUploadSessionStatus.Queued)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.Completed)
                    .SetProperty(s => s.CreatedDocumentId, existingDoc.DocumentId)
                    .SetProperty(s => s.CreatedBatchId, existingBatch.BatchId)
                    .SetProperty(s => s.CompletedUtc, DateTime.UtcNow)
                    .SetProperty(s => s.ActiveFinalizationExpiresUtc, (DateTime?)null)
                    .SetProperty(s => s.FinalizationAttemptId, (Guid?)null), ct);

            if (completed == 0)
            {
                throw new InvalidOperationException($"CAS transition to Completed failed for session {session.SessionId}. Attempt may have expired or been superseded.");
            }
            session.Status = LargeArchiveUploadSessionStatus.Completed;
        }

        // Release large upload slot
        await slotLeaseService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, session.SessionId.ToString(), ct);

        return (existingDoc.DocumentId, existingBatch.BatchId);
    }

    public async Task<int> ReconcileIncompleteFinalizationsAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var staleSessions = await db.LargeArchiveUploadSessions
            .Where(s => (s.Status == LargeArchiveUploadSessionStatus.Verifying && (s.FinalizationAttemptId == null || s.ActiveFinalizationExpiresUtc < now))
                     || ((s.Status == LargeArchiveUploadSessionStatus.ArchiveMoved
                          || s.Status == LargeArchiveUploadSessionStatus.DocumentCommitted
                          || s.Status == LargeArchiveUploadSessionStatus.BatchCreated
                          || s.Status == LargeArchiveUploadSessionStatus.Queued)
                         && (s.FinalizationAttemptId == null || s.ActiveFinalizationExpiresUtc < now)))
            .Select(s => s.SessionId)
            .ToListAsync(ct);

        var recovered = 0;
        foreach (var sessionId in staleSessions)
        {
            try
            {
                var attemptId = Guid.NewGuid();
                var claimed = await db.LargeArchiveUploadSessions
                    .Where(s => s.SessionId == sessionId && (s.FinalizationAttemptId == null || s.ActiveFinalizationExpiresUtc < DateTime.UtcNow))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.FinalizationAttemptId, attemptId)
                        .SetProperty(s => s.ActiveFinalizationExpiresUtc, DateTime.UtcNow.AddMinutes(5))
                        .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

                if (claimed == 0) continue;

                var session = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId, ct);

                if (session.Status == LargeArchiveUploadSessionStatus.Verifying)
                {
                    if (File.Exists(session.DestinationStoragePath))
                    {
                        // File move succeeded before crash
                        await db.LargeArchiveUploadSessions
                            .Where(s => s.SessionId == sessionId && s.FinalizationAttemptId == attemptId)
                            .ExecuteUpdateAsync(u => u.SetProperty(s => s.Status, LargeArchiveUploadSessionStatus.ArchiveMoved), ct);
                        session.Status = LargeArchiveUploadSessionStatus.ArchiveMoved;
                    }
                    else
                    {
                        await ExecuteFencedMoveAsync(session, attemptId, ct);
                        session.Status = LargeArchiveUploadSessionStatus.ArchiveMoved;
                    }
                }

                await CommitDocumentAndBatchAsync(session, attemptId, ct);
                recovered++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to reconcile stale finalization for session {SessionId}", sessionId);
            }
        }

        return recovered;
    }
}
