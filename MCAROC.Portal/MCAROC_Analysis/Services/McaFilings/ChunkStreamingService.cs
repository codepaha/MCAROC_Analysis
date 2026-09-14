using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.McaFilings;

public record ChunkWriteResult(bool Success, long AcceptedOffset, string? Error, int StatusCode);

public class ChunkStreamingService(
    AppDbContext db,
    IOptions<LargeArchiveUploadOptions> options,
    ILogger<ChunkStreamingService> logger)
{
    private readonly LargeArchiveUploadOptions _opts = options.Value;

    public async Task<ChunkWriteResult> WriteChunkAsync(
        Guid sessionId,
        string clientToken,
        long offset,
        long chunkSize,
        string declaredChunkSha256,
        Stream incomingBodyStream,
        CancellationToken ct)
    {
        var session = await db.LargeArchiveUploadSessions
            .FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        if (session is null)
            return new ChunkWriteResult(false, 0, "Upload session not found.", 404);

        if (!VerifyToken(session.HashedCapabilityToken, clientToken))
            return new ChunkWriteResult(false, session.NextExpectedOffset, "Invalid upload capability token.", 403);

        if (session.Status != LargeArchiveUploadSessionStatus.Uploading)
            return new ChunkWriteResult(false, session.NextExpectedOffset, $"Session is in '{session.Status}' state, not Uploading.", 400);

        if (DateTime.UtcNow > session.ExpiresUtc)
        {
            session.Status = LargeArchiveUploadSessionStatus.Expired;
            await db.SaveChangesAsync(ct);
            return new ChunkWriteResult(false, session.NextExpectedOffset, "Upload session has expired.", 410);
        }

        // Validate chunk size bounds
        var expectedChunkBytes = Math.Min(_opts.ChunkSizeBytes, session.TotalExpectedSizeBytes - offset);
        if (chunkSize != expectedChunkBytes)
            return new ChunkWriteResult(false, session.NextExpectedOffset, $"Declared chunk size {chunkSize} does not match expected size {expectedChunkBytes}.", 400);

        // 1. Acquire durable pre-write lease
        var attemptId = Guid.NewGuid();
        var leaseDuration = TimeSpan.FromMinutes(5);
        var leaseExpires = DateTime.UtcNow.Add(leaseDuration);

        var acquired = await db.LargeArchiveUploadSessions
            .Where(s => s.SessionId == sessionId
                && s.Status == LargeArchiveUploadSessionStatus.Uploading
                && s.NextExpectedOffset == offset
                && (s.ActiveWriteAttemptId == null || s.ActiveWriteExpiresUtc < DateTime.UtcNow))
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ActiveWriteAttemptId, attemptId)
                .SetProperty(s => s.ActiveWriteOffset, offset)
                .SetProperty(s => s.ActiveWriteExpiresUtc, leaseExpires)
                .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

        if (acquired == 0)
        {
            // Re-read to provide precise feedback
            var current = await db.LargeArchiveUploadSessions.AsNoTracking().FirstAsync(s => s.SessionId == sessionId, ct);
            if (current.NextExpectedOffset > offset)
                return new ChunkWriteResult(false, current.NextExpectedOffset, "Offset already accepted.", 409);
            if (current.ActiveWriteAttemptId != null && current.ActiveWriteExpiresUtc > DateTime.UtcNow)
                return new ChunkWriteResult(false, current.NextExpectedOffset, "A concurrent chunk write attempt is actively in progress.", 409);

            return new ChunkWriteResult(false, current.NextExpectedOffset, $"Offset mismatch. Expected {current.NextExpectedOffset}, got {offset}.", 409);
        }

        // 2. Open staging file under lease and reconcile uncommitted append tail if any
        var stagingDir = Path.GetDirectoryName(session.StagingFilePath)!;
        Directory.CreateDirectory(stagingDir);

        try
        {
            using (var archiveStream = new FileStream(session.StagingFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                if (archiveStream.Length > offset)
                {
                    logger.LogWarning("Staging archive.part length ({Length}) exceeds committed offset ({Offset}) for session {SessionId}; truncating uncommitted tail.",
                        archiveStream.Length, offset, sessionId);
                    archiveStream.SetLength(offset);
                    archiveStream.Flush(true);
                }
                else if (archiveStream.Length < offset)
                {
                    logger.LogError("Staging archive.part length ({Length}) is behind committed offset ({Offset}) for session {SessionId}. Corrupted staging file.",
                        archiveStream.Length, offset, sessionId);
                    return new ChunkWriteResult(false, archiveStream.Length, "Physical staging file was truncated or corrupted.", 500);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reconcile staging file before writing chunk {Offset}", offset);
            await ReleaseWriteLeaseAsync(sessionId, attemptId, ct);
            return new ChunkWriteResult(false, offset, $"Staging file lock error: {ex.Message}", 500);
        }

        // 3. Stream incoming chunk to isolated attempt-specific temp file
        var tempChunkPath = Path.Combine(stagingDir, $"chunk_{offset}_{attemptId:N}.tmp");
        string computedChunkHash;
        long bytesWritten = 0;

        try
        {
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var tempStream = new FileStream(tempChunkPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[64 * 1024]; // 64 KiB transfer buffer
                int read;
                while ((read = await incomingBodyStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                {
                    bytesWritten += read;
                    if (bytesWritten > expectedChunkBytes)
                    {
                        throw new InvalidOperationException($"Chunk stream exceeded expected byte size of {expectedChunkBytes}.");
                    }
                    sha.AppendData(buffer, 0, read);
                    await tempStream.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                computedChunkHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            }

            if (bytesWritten != expectedChunkBytes)
            {
                throw new InvalidOperationException($"Incomplete chunk stream. Expected {expectedChunkBytes} bytes, received {bytesWritten}.");
            }

            var expectedHashNormalized = declaredChunkSha256.Trim().ToLowerInvariant();
            if (!string.Equals(computedChunkHash, expectedHashNormalized, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Chunk SHA-256 mismatch. Declared: {expectedHashNormalized}, Computed: {computedChunkHash}.");
            }

            // 4. Append validated chunk to archive.part
            await using (var archiveStream = new FileStream(session.StagingFilePath, FileMode.Open, FileAccess.Write, FileShare.None))
            await using (var tempReadStream = new FileStream(tempChunkPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                archiveStream.Seek(offset, SeekOrigin.Begin);
                await tempReadStream.CopyToAsync(archiveStream, ct);
                await archiveStream.FlushAsync(ct);
            }

            // 5. Atomic database CAS to commit offset and release write lease
            var newOffset = offset + bytesWritten;
            var committed = await db.LargeArchiveUploadSessions
                .Where(s => s.SessionId == sessionId && s.ActiveWriteAttemptId == attemptId)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.NextExpectedOffset, newOffset)
                    .SetProperty(s => s.ActiveWriteAttemptId, (Guid?)null)
                    .SetProperty(s => s.ActiveWriteOffset, (long?)null)
                    .SetProperty(s => s.ActiveWriteExpiresUtc, (DateTime?)null)
                    .SetProperty(s => s.LastHeartbeatUtc, DateTime.UtcNow), ct);

            if (committed == 0)
            {
                throw new InvalidOperationException("Failed to commit offset update in database; lease was lost or expired.");
            }

            // Delete attempt temp file ONLY after database CAS succeeds
            try { File.Delete(tempChunkPath); } catch { /* best effort */ }

            return new ChunkWriteResult(true, newOffset, null, 200);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(tempChunkPath)) File.Delete(tempChunkPath); } catch { /* best effort */ }
            await ReleaseWriteLeaseAsync(sessionId, attemptId, ct);
            logger.LogWarning(ex, "Chunk write failed for session {SessionId} at offset {Offset}", sessionId, offset);
            return new ChunkWriteResult(false, offset, ex.Message, 400);
        }
    }

    private async Task ReleaseWriteLeaseAsync(Guid sessionId, Guid attemptId, CancellationToken ct)
    {
        await db.LargeArchiveUploadSessions
            .Where(s => s.SessionId == sessionId && s.ActiveWriteAttemptId == attemptId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(s => s.ActiveWriteAttemptId, (Guid?)null)
                .SetProperty(s => s.ActiveWriteOffset, (long?)null)
                .SetProperty(s => s.ActiveWriteExpiresUtc, (DateTime?)null), ct);
    }

    public static string ComputeTokenHash(string token)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(token);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifyToken(string storedHash, string rawToken)
    {
        var computed = ComputeTokenHash(rawToken);
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(storedHash.ToLowerInvariant()),
            System.Text.Encoding.UTF8.GetBytes(computed));
    }
}
