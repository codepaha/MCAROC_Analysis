using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Controllers;

public record InitiateUploadRequest(string FileName, long TotalSizeBytes, string ExpectedFullSha256);
public record InitiateUploadResponse(Guid SessionId, string CapabilityToken, int ChunkSizeBytes, long MaxArchiveSizeBytes);
public record SessionStatusResponse(Guid SessionId, string Status, long NextExpectedOffset, long TotalSizeBytes, bool IsComplete);

[ApiController]
[Route("Requests/{requestId:long}/uploads/filings")]
public class RequestsUploadController(
    AppDbContext db,
    IOptions<LargeArchiveUploadOptions> options,
    IStorageReservationManager reservationManager,
    IOperationalSlotLeaseService slotLeaseService,
    ChunkStreamingService chunkStreamingService,
    FinalizationRecoveryService finalizationService,
    IWebHostEnvironment env,
    ILogger<RequestsUploadController> logger) : ControllerBase
{
    private readonly LargeArchiveUploadOptions _opts = options.Value;

    [HttpPost("initiate")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Initiate(long requestId, [FromBody] InitiateUploadRequest request, CancellationToken ct)
    {
        if (!_opts.Enabled)
            return StatusCode(StatusCodes.Status403Forbidden, "Large archive uploads are currently disabled by configuration.");

        if (string.IsNullOrWhiteSpace(request.FileName) || !request.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest("File must be a valid .zip archive.");

        if (request.TotalSizeBytes <= 0 || request.TotalSizeBytes > _opts.MaxArchiveSizeBytes)
            return BadRequest($"Archive size must be between 1 byte and {_opts.MaxArchiveSizeBytes / (1024 * 1024 * 1024)} GiB.");

        if (string.IsNullOrWhiteSpace(request.ExpectedFullSha256) || request.ExpectedFullSha256.Length != 64)
            return BadRequest("A valid 64-character SHA-256 hash manifest is required.");

        var mcaRequest = await db.Requests.FirstOrDefaultAsync(r => r.RequestId == requestId, ct);
        if (mcaRequest is null)
            return NotFound("Request not found.");

        var sessionId = Guid.NewGuid();

        // 1. Acquire a large upload slot lease — up to MaxConcurrentUploads sessions may hold one at once.
        var slotResult = await slotLeaseService.TryAcquireSlotAsync(
            OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), _opts.SlotLeaseDuration, _opts.MaxConcurrentUploads, ct);

        if (!slotResult.Success)
            return StatusCode(StatusCodes.Status429TooManyRequests, slotResult.Error ?? "Another large upload is currently in progress.");

        // 2. Resolve paths and reserve volume storage capacity
        var stagingDir = Path.Combine(env.ContentRootPath, "App_Data", "Requests", requestId.ToString(), "mca-filings", "staging", sessionId.ToString());
        var destDir = Path.Combine(env.ContentRootPath, "App_Data", "Uploads", requestId.ToString(), "original");
        var stagingPartPath = Path.Combine(stagingDir, "archive.part");
        var destZipPath = Path.Combine(destDir, $"{sessionId:N}.zip");

        var reservation = await reservationManager.TryReserveUploadCapacityAsync(
            sessionId, request.TotalSizeBytes, stagingDir, destDir, ct);

        if (!reservation.Success)
        {
            await slotLeaseService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), ct);
            return StatusCode(StatusCodes.Status507InsufficientStorage, reservation.Error ?? "Storage capacity reservation failed.");
        }

        // 3. Create upload session with opaque token
        var rawToken = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        var hashedToken = ChunkStreamingService.ComputeTokenHash(rawToken);

        var session = new LargeArchiveUploadSession
        {
            SessionId = sessionId,
            RequestId = requestId,
            HashedCapabilityToken = hashedToken,
            OriginalFileName = Path.GetFileName(request.FileName),
            TotalExpectedSizeBytes = request.TotalSizeBytes,
            NextExpectedOffset = 0,
            ExpectedFullSha256 = request.ExpectedFullSha256.Trim().ToLowerInvariant(),
            Status = LargeArchiveUploadSessionStatus.Uploading,
            StagingFilePath = stagingPartPath,
            DestinationStoragePath = destZipPath,
            CreatedUtc = DateTime.UtcNow,
            LastHeartbeatUtc = DateTime.UtcNow,
            ExpiresUtc = DateTime.UtcNow.Add(_opts.SessionLifetime)
        };

        db.LargeArchiveUploadSessions.Add(session);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Initiated resumable upload session {SessionId} for Request {RequestId}, Size={Size}", sessionId, requestId, request.TotalSizeBytes);

        return Ok(new InitiateUploadResponse(sessionId, rawToken, _opts.ChunkSizeBytes, _opts.MaxArchiveSizeBytes));
    }

    [HttpGet("session/{sessionId:guid}")]
    public async Task<IActionResult> GetStatus(long requestId, Guid sessionId, [FromHeader(Name = "X-Upload-Token")] string? token, CancellationToken ct)
    {
        var session = await db.LargeArchiveUploadSessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId && s.RequestId == requestId, ct);
        if (session is null) return NotFound("Session not found.");

        if (string.IsNullOrWhiteSpace(token) || !ChunkStreamingService.VerifyToken(session.HashedCapabilityToken, token))
            return StatusCode(StatusCodes.Status403Forbidden, "Invalid capability token.");

        var isComplete = session.Status == LargeArchiveUploadSessionStatus.Completed;
        return Ok(new SessionStatusResponse(session.SessionId, session.Status.ToString(), session.NextExpectedOffset, session.TotalExpectedSizeBytes, isComplete));
    }

    [HttpPost("session/{sessionId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(long requestId, Guid sessionId, [FromHeader(Name = "X-Upload-Token")] string? token, CancellationToken ct)
    {
        var session = await db.LargeArchiveUploadSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.RequestId == requestId, ct);
        if (session is null) return NotFound();

        if (string.IsNullOrWhiteSpace(token) || !ChunkStreamingService.VerifyToken(session.HashedCapabilityToken, token))
            return StatusCode(StatusCodes.Status403Forbidden);

        await slotLeaseService.TryRenewSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), _opts.SlotLeaseDuration, ct);

        session.LastHeartbeatUtc = DateTime.UtcNow;
        session.ExpiresUtc = DateTime.UtcNow.Add(_opts.SessionLifetime);
        await db.SaveChangesAsync(ct);

        return Ok();
    }

    [HttpPut("session/{sessionId:guid}/chunk")]
    [RequestSizeLimit(LargeArchiveUploadOptions.MaxChunkRequestSizeBytes)]
    public async Task<IActionResult> UploadChunk(
        long requestId,
        Guid sessionId,
        [FromHeader(Name = "X-Upload-Token")] string? token,
        [FromHeader(Name = "X-Chunk-Offset")] long? offset,
        [FromHeader(Name = "X-Chunk-Size")] long? chunkSize,
        [FromHeader(Name = "X-Chunk-Sha256")] string? chunkSha256,
        CancellationToken ct)
    {
        if (Request.HasFormContentType)
            return StatusCode(StatusCodes.Status415UnsupportedMediaType, "Chunk upload endpoint accepts raw octet-stream only, not multipart/form-data.");

        if (string.IsNullOrWhiteSpace(token)) return StatusCode(StatusCodes.Status403Forbidden, "Upload token header 'X-Upload-Token' is required.");
        if (!offset.HasValue) return BadRequest("Chunk offset header 'X-Chunk-Offset' is required.");
        if (!chunkSize.HasValue) return BadRequest("Chunk size header 'X-Chunk-Size' is required.");
        if (string.IsNullOrWhiteSpace(chunkSha256)) return BadRequest("Chunk SHA-256 header 'X-Chunk-Sha256' is required.");

        // Renew large upload slot lease on every active chunk
        await slotLeaseService.TryRenewSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), _opts.SlotLeaseDuration, ct);

        var result = await chunkStreamingService.WriteChunkAsync(
            sessionId, token, offset.Value, chunkSize.Value, chunkSha256, Request.Body, ct);

        if (!result.Success)
            return StatusCode(result.StatusCode, result.Error);

        return Ok(new { AcceptedOffset = result.AcceptedOffset });
    }

    [HttpPost("session/{sessionId:guid}/complete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Complete(long requestId, Guid sessionId, [FromHeader(Name = "X-Upload-Token")] string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return StatusCode(StatusCodes.Status403Forbidden, "Upload token header 'X-Upload-Token' is required.");

        var result = await finalizationService.CompleteSessionAsync(sessionId, token, ct);
        if (!result.Success)
            return StatusCode(result.StatusCode, result.Error);

        return Ok(new { Success = true, DocumentId = result.DocumentId, BatchId = result.BatchId });
    }

    [HttpPost("session/{sessionId:guid}/abort")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Abort(long requestId, Guid sessionId, [FromHeader(Name = "X-Upload-Token")] string? token, CancellationToken ct)
    {
        var session = await db.LargeArchiveUploadSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId && s.RequestId == requestId, ct);
        if (session is null) return NotFound();

        if (string.IsNullOrWhiteSpace(token) || !ChunkStreamingService.VerifyToken(session.HashedCapabilityToken, token))
            return StatusCode(StatusCodes.Status403Forbidden);

        session.Status = LargeArchiveUploadSessionStatus.Aborted;
        await db.SaveChangesAsync(ct);

        try
        {
            if (System.IO.File.Exists(session.StagingFilePath))
                System.IO.File.Delete(session.StagingFilePath);

            var dir = Path.GetDirectoryName(session.StagingFilePath);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch { /* best effort */ }

        await reservationManager.ReleaseReservationsAsync("UploadSession", sessionId.ToString(), ct);
        await slotLeaseService.ReleaseSlotAsync(OperationalSlotLeaseService.LargeUploadSlot, sessionId.ToString(), ct);

        return Ok(new { Aborted = true });
    }
}
