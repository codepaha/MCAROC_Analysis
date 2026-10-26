using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Excel;

public interface IWorkbookDerivativeService
{
    Task<RequestDocumentDerivative?> GetOrCreateSanitizedDerivativeAsync(RequestDocument doc, CancellationToken ct = default);
    Task<int> CleanupOrphanGenerationsAsync(long requestId, TimeSpan? olderThan = null, CancellationToken ct = default);
}

public class WorkbookDerivativeService(
    AppDbContext db,
    IWebHostEnvironment env,
    FileValidationService fileValidation,
    ILogger<WorkbookDerivativeService> logger,
    MCAROC_Analysis.Services.Storage.IStoragePathResolver? pathResolver = null) : IWorkbookDerivativeService
{
    private readonly MCAROC_Analysis.Services.Storage.IStoragePathResolver _pathResolver =
        pathResolver ?? (env != null ? new MCAROC_Analysis.Services.Storage.StoragePathResolver(env, null) : MCAROC_Analysis.Services.Storage.StoragePathHelper.Default);
    public const int CurrentSanitizerVersion = 1;

    /// <summary>
    /// Test seam allowing deterministic simulation of lease expiration or takeover before validation.
    /// </summary>
    internal Func<long, Guid, Task>? PreValidationHook { get; set; }

    /// <summary>
    /// Test seam allowing deterministic simulation of lease expiration or takeover before the completion CAS.
    /// </summary>
    internal Func<long, Guid, Task>? PreCasCompletionHook { get; set; }

    public async Task<RequestDocumentDerivative?> GetOrCreateSanitizedDerivativeAsync(RequestDocument doc, CancellationToken ct = default)
    {
        if (doc == null) return null;

        var ext = Path.GetExtension(doc.OriginalFileName);
        if (string.IsNullOrWhiteSpace(ext) || (!ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)))
        {
            ext = Path.GetExtension(doc.StoragePath);
            if (string.IsNullOrWhiteSpace(ext) || (!ext.Equals(".xls", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
        }

        var existing = await db.RequestDocumentDerivatives
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);

        if (existing != null &&
            existing.Status == DocumentDerivativeStatus.Ready &&
            existing.SanitizerVersion == CurrentSanitizerVersion &&
            existing.RawFileHash == doc.FileHash &&
            File.Exists(_pathResolver.ResolveFullPath(existing.StoragePath)))
        {
            return existing;
        }

        if (existing == null)
        {
            var workerToken = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var newDerivative = new RequestDocumentDerivative
            {
                DocumentId = doc.DocumentId,
                RequestId = doc.RequestId,
                DerivativeType = DocumentDerivativeType.SanitizedExcel,
                SanitizerVersion = CurrentSanitizerVersion,
                RawFileHash = doc.FileHash ?? string.Empty,
                Status = DocumentDerivativeStatus.Pending,
                LeaseToken = workerToken,
                LeaseExpiresUtc = now.AddMinutes(3),
                CreatedUtc = now,
                UpdatedUtc = now,
                StoragePath = string.Empty,
                FileHash = string.Empty
            };

            try
            {
                db.RequestDocumentDerivatives.Add(newDerivative);
                await db.SaveChangesAsync(ct);
                return await GenerateSanitizedDerivativeAsync(newDerivative.DerivativeId, workerToken, doc, ext, ct);
            }
            catch (DbUpdateException)
            {
                existing = await db.RequestDocumentDerivatives
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);
            }
        }

        if (existing != null &&
            existing.Status == DocumentDerivativeStatus.Ready &&
            existing.SanitizerVersion == CurrentSanitizerVersion &&
            existing.RawFileHash == doc.FileHash &&
            File.Exists(_pathResolver.ResolveFullPath(existing.StoragePath)))
        {
            return existing;
        }

        if (existing != null)
        {
            var now = DateTime.UtcNow;
            var canClaim = existing.Status == DocumentDerivativeStatus.Failed ||
                           (existing.Status == DocumentDerivativeStatus.Pending && existing.LeaseExpiresUtc.HasValue && existing.LeaseExpiresUtc.Value < now) ||
                           (existing.Status == DocumentDerivativeStatus.Ready && (!File.Exists(_pathResolver.ResolveFullPath(existing.StoragePath)) || existing.RawFileHash != doc.FileHash));

            if (canClaim)
            {
                var workerToken = Guid.NewGuid();
                var newLeaseExpiry = now.AddMinutes(3);

                var rowsClaimed = await db.RequestDocumentDerivatives
                    .Where(d => d.DerivativeId == existing.DerivativeId &&
                                (d.Status == DocumentDerivativeStatus.Failed ||
                                 (d.Status == DocumentDerivativeStatus.Pending && d.LeaseExpiresUtc != null && d.LeaseExpiresUtc < now) ||
                                 (d.Status == DocumentDerivativeStatus.Ready && d.RawFileHash != doc.FileHash)))
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DocumentDerivativeStatus.Pending)
                        .SetProperty(d => d.LeaseToken, workerToken)
                        .SetProperty(d => d.LeaseExpiresUtc, newLeaseExpiry)
                        .SetProperty(d => d.RawFileHash, doc.FileHash ?? string.Empty)
                        .SetProperty(d => d.SanitizerVersion, CurrentSanitizerVersion)
                        .SetProperty(d => d.UpdatedUtc, now), ct);

                if (rowsClaimed == 1)
                {
                    return await GenerateSanitizedDerivativeAsync(existing.DerivativeId, workerToken, doc, ext, ct);
                }
            }
        }

        return await db.RequestDocumentDerivatives
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);
    }

    private async Task<RequestDocumentDerivative?> GenerateSanitizedDerivativeAsync(
        long derivativeId,
        Guid workerToken,
        RequestDocument doc,
        string ext,
        CancellationToken ct)
    {
        var derivativesDir = _pathResolver.GetDerivativesDir(doc.RequestId);
        Directory.CreateDirectory(derivativesDir);
        var generationPath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{workerToken:N}{ext}");

        try
        {
            var fullDocPath = _pathResolver.ResolveFullPath(doc.StoragePath);
            if (string.IsNullOrWhiteSpace(fullDocPath) || !File.Exists(fullDocPath))
            {
                logger.LogWarning("Source document file not found at {StoragePath} for Document {DocumentId}", fullDocPath, doc.DocumentId);
                var failureTime = DateTime.UtcNow;
                var sourceMissingRows = await db.RequestDocumentDerivatives
                    .Where(d => d.DerivativeId == derivativeId
                             && d.LeaseToken == workerToken
                             && d.LeaseExpiresUtc > failureTime)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                        .SetProperty(d => d.ErrorMessage, "Source document file not found.")
                        .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                        .SetProperty(d => d.UpdatedUtc, failureTime), ct);

                if (sourceMissingRows == 0)
                {
                    logger.LogWarning("Worker {Token} lease expired or lost before recording missing source failure for derivative {DerivativeId}.", workerToken, derivativeId);
                }

                return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
            }

            File.Copy(fullDocPath, generationPath, overwrite: true);

            ExcelMetadataSanitizer.SanitizeWorkbook(generationPath);

            if (PreValidationHook != null)
            {
                await PreValidationHook(derivativeId, workerToken);
            }

            var openCheck = fileValidation.ValidateOpens(generationPath);
            if (!openCheck.IsValid)
            {
                if (File.Exists(generationPath))
                {
                    try { File.Delete(generationPath); } catch { }
                }

                logger.LogWarning("Sanitized derivative failed validation for Document {DocumentId}: {Error}", doc.DocumentId, openCheck.Error);
                var failureTime = DateTime.UtcNow;
                var validationFailureRows = await db.RequestDocumentDerivatives
                    .Where(d => d.DerivativeId == derivativeId
                             && d.LeaseToken == workerToken
                             && d.LeaseExpiresUtc > failureTime)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                        .SetProperty(d => d.ErrorMessage, openCheck.Error ?? "Validation failed.")
                        .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                        .SetProperty(d => d.UpdatedUtc, failureTime), ct);

                if (validationFailureRows == 0)
                {
                    logger.LogWarning("Worker {Token} lease expired or lost before recording validation failure for derivative {DerivativeId}.", workerToken, derivativeId);
                }

                return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
            }

            var fileInfo = new FileInfo(generationPath);
            string fileHash;
            using (var fs = File.OpenRead(generationPath))
            {
                fileHash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            }

            if (PreCasCompletionHook != null)
            {
                await PreCasCompletionHook(derivativeId, workerToken);
            }

            var completionTime = DateTime.UtcNow;
            var rowsUpdated = await db.RequestDocumentDerivatives
                .Where(d => d.DerivativeId == derivativeId
                         && d.LeaseToken == workerToken
                         && d.LeaseExpiresUtc > completionTime)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DocumentDerivativeStatus.Ready)
                    .SetProperty(d => d.StoragePath, _pathResolver.ToRelativePath(generationPath))
                    .SetProperty(d => d.FileSize, fileInfo.Length)
                    .SetProperty(d => d.FileHash, fileHash)
                    .SetProperty(d => d.ErrorMessage, (string?)null)
                    .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(d => d.UpdatedUtc, completionTime), ct);

            if (rowsUpdated == 1)
            {
                logger.LogInformation("Sanitized derivative ready for Document {DocumentId} at {StoragePath}", doc.DocumentId, generationPath);
                return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
            }

            // Lost lease or lease expired before CAS: delete own generation file and do not publish!
            logger.LogWarning("Worker {Token} lost lease or lease expired before completion CAS for derivative {DerivativeId}; generation file cleaned up.", workerToken, derivativeId);
            if (File.Exists(generationPath))
            {
                try { File.Delete(generationPath); } catch { }
            }

            return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error generating derivative for Document {DocumentId}", doc.DocumentId);
            if (File.Exists(generationPath))
            {
                try { File.Delete(generationPath); } catch { }
            }

            var failureTime = DateTime.UtcNow;
            var exceptionRows = await db.RequestDocumentDerivatives
                .Where(d => d.DerivativeId == derivativeId
                         && d.LeaseToken == workerToken
                         && d.LeaseExpiresUtc > failureTime)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                    .SetProperty(d => d.ErrorMessage, ex.Message)
                    .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(d => d.UpdatedUtc, failureTime), ct);

            if (exceptionRows == 0)
            {
                logger.LogWarning("Worker {Token} lease expired or lost before recording exception failure for derivative {DerivativeId}.", workerToken, derivativeId);
            }

            return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
        }
    }

    public async Task<int> CleanupOrphanGenerationsAsync(long requestId, TimeSpan? olderThan = null, CancellationToken ct = default)
    {
        var derivativesDir = _pathResolver.GetDerivativesDir(requestId);
        if (!Directory.Exists(derivativesDir)) return 0;

        var threshold = DateTime.UtcNow - (olderThan ?? TimeSpan.FromHours(1));
        var activePaths = await db.RequestDocumentDerivatives
            .Where(d => d.RequestId == requestId && d.Status == DocumentDerivativeStatus.Ready)
            .Select(d => d.StoragePath)
            .ToListAsync(ct);

        var activeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in activePaths)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var fullPath = _pathResolver.ResolveFullPath(p);
            activeSet.Add(Path.GetFullPath(fullPath));
        }

        var deletedCount = 0;
        var files = Directory.GetFiles(derivativesDir);
        foreach (var file in files)
        {
            var normalizedFile = Path.GetFullPath(file);
            if (!_pathResolver.IsContained(normalizedFile, derivativesDir)) continue;
            if (activeSet.Contains(normalizedFile)) continue;

            var fileInfo = new FileInfo(normalizedFile);
            if (fileInfo.LastWriteTimeUtc < threshold)
            {
                try
                {
                    File.Delete(normalizedFile);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete orphan derivative file {File}", normalizedFile);
                }
            }
        }

        return deletedCount;
    }
}
