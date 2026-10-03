using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.AutoFetch;

public enum DocumentRestoreStatus
{
    Success,
    NotRetired,
    MissingSourceInfo,
    SourceExpired,
    SessionUnavailable,
    DownloadFailed,
    HashMismatch,
    Error
}

public sealed record DocumentRestoreResult(DocumentRestoreStatus Status, string? RestoredPath, string? ErrorMessage);

/// <summary>
/// Service to re-download and restore retired MCA filing PDFs on demand (#376).
/// Validates company unlock window (1 year - 1 day), fetches PDF via ReferenceToolClient,
/// verifies SHA-256 against stored FileHash, and restores to disk and database.
/// </summary>
public sealed class McaDocumentRestoreService(
    AppDbContext db,
    ReferenceToolClient client,
    IHostEnvironment env,
    IOptions<ReferenceToolOptions> options,
    TimeProvider timeProvider,
    ILogger<McaDocumentRestoreService> logger)
{
    private readonly ReferenceToolOptions _opts = options.Value;

    public async Task<DocumentRestoreResult> RestoreDocumentAsync(McaFilingDocument document, CancellationToken ct = default)
    {
        // 1. Check if document is retired
        if (document.RetiredUtc == null && !string.IsNullOrWhiteSpace(document.StoragePath) && File.Exists(document.StoragePath))
        {
            return new DocumentRestoreResult(DocumentRestoreStatus.NotRetired, document.StoragePath, null);
        }

        if (string.IsNullOrWhiteSpace(document.SourceAwsPath))
        {
            return new DocumentRestoreResult(DocumentRestoreStatus.MissingSourceInfo, null, "Document does not have source reference information for re-download.");
        }

        // 2. Identify company and check 1-year unlock window
        var request = await db.Requests.AsNoTracking().FirstOrDefaultAsync(r => r.RequestId == document.RequestId, ct);
        if (request == null)
        {
            return new DocumentRestoreResult(DocumentRestoreStatus.Error, null, $"Request {document.RequestId} not found.");
        }

        var identifier = (request.Cin ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(identifier))
        {
            return new DocumentRestoreResult(DocumentRestoreStatus.Error, null, "Request CIN is missing.");
        }

        var lifecycle = await db.CompanyReportLifecycles.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Identifier == identifier, ct);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (lifecycle?.UnlockedUtc is { } unlockedUtc)
        {
            var validTill = CompanyRefreshService.UnlockValidTill(unlockedUtc);
            if (now > validTill)
            {
                logger.LogWarning("Cannot restore document {DocId}: 1-year reference tool unlock expired on {ValidTill}.", document.FilingDocumentId, validTill);
                return new DocumentRestoreResult(
                    DocumentRestoreStatus.SourceExpired,
                    null,
                    $"The reference tool unlock for {identifier} expired on {validTill:yyyy-MM-dd}. Re-unlock the company to restore original PDFs.");
            }
        }

        // 3. Resolve bid and userId
        var bid = ReferenceToolClient.ComputeBid(identifier);
        string userId = !string.IsNullOrWhiteSpace(_opts.UserId) ? _opts.UserId.Trim() : string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
        {
            try
            {
                var session = await client.RequireValidSessionAsync(ct);
                userId = session.UserId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(userId))
                {
                    return new DocumentRestoreResult(DocumentRestoreStatus.SessionUnavailable, null, "User ID could not be resolved from session.");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to obtain valid session for document restore.");
                return new DocumentRestoreResult(DocumentRestoreStatus.SessionUnavailable, null, "The reference tool session is unavailable or expired.");
            }
        }

        // 4. Determine destination path
        var documentsDir = FilingStoragePaths.DocumentsDir(env.ContentRootPath, document.RequestId, document.BatchId, document.FilingId);
        Directory.CreateDirectory(documentsDir);
        var targetDocFile = $"{Guid.NewGuid():N}.pdf";
        var destinationPath = Path.Combine(documentsDir, targetDocFile);

        try
        {
            var did = document.SourceDocId ?? document.FilingDocumentId.ToString();
            await client.DownloadPdfAsync(bid, userId, document.SourceAwsPath, did, destinationPath, ct);

            if (!File.Exists(destinationPath))
            {
                return new DocumentRestoreResult(DocumentRestoreStatus.DownloadFailed, null, "Downloaded file was not created.");
            }

            // 5. Verify SHA-256 against stored FileHash
            string computedHash;
            await using (var fs = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true))
            {
                computedHash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            }

            if (!string.Equals(computedHash, document.FileHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError("Hash mismatch on restored document {DocId}. Expected {Expected}, computed {Actual}. Discarding downloaded file.",
                    document.FilingDocumentId, document.FileHash, computedHash);
                try { File.Delete(destinationPath); } catch { /* ignore */ }
                return new DocumentRestoreResult(DocumentRestoreStatus.HashMismatch, null, "Downloaded PDF hash does not match the original file hash.");
            }

            // 6. Update database record
            var rowsUpdated = await db.McaFilingDocuments
                .Where(d => d.FilingDocumentId == document.FilingDocumentId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.StoragePath, destinationPath)
                    .SetProperty(d => d.RetiredUtc, (DateTime?)null)
                    .SetProperty(d => d.UpdatedAt, now), ct);

            document.StoragePath = destinationPath;
            document.RetiredUtc = null;
            document.UpdatedAt = now;

            logger.LogInformation("Successfully restored document {DocId} to {Path}", document.FilingDocumentId, destinationPath);
            return new DocumentRestoreResult(DocumentRestoreStatus.Success, destinationPath, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Failed to re-download document {DocId} from reference tool.", document.FilingDocumentId);
            try { if (File.Exists(destinationPath)) File.Delete(destinationPath); } catch { /* ignore */ }
            return new DocumentRestoreResult(DocumentRestoreStatus.DownloadFailed, null, $"Download failed: {ex.Message}");
        }
    }
}
