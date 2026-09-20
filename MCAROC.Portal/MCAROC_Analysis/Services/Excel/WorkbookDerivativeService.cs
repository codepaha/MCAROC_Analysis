using System.Security.Cryptography;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Excel;

public interface IWorkbookDerivativeService
{
    Task<RequestDocumentDerivative?> GetOrCreateSanitizedDerivativeAsync(RequestDocument doc, CancellationToken ct = default);
}

public class WorkbookDerivativeService(
    AppDbContext db,
    IWebHostEnvironment env,
    FileValidationService fileValidation,
    ILogger<WorkbookDerivativeService> logger) : IWorkbookDerivativeService
{
    public const int CurrentSanitizerVersion = 1;

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
            File.Exists(existing.StoragePath))
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
                db.Entry(newDerivative).State = EntityState.Detached;
                existing = await db.RequestDocumentDerivatives
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);

                if (existing == null)
                {
                    logger.LogWarning("Failed to create or reload derivative record for Document {DocumentId}", doc.DocumentId);
                    return null;
                }
            }
        }

        if (existing.Status == DocumentDerivativeStatus.Ready &&
            existing.SanitizerVersion == CurrentSanitizerVersion &&
            existing.RawFileHash == doc.FileHash &&
            !string.IsNullOrEmpty(existing.StoragePath) &&
            File.Exists(existing.StoragePath))
        {
            return existing;
        }

        // If another worker holds an active lease, wait briefly to observe completion
        if (existing.Status == DocumentDerivativeStatus.Pending && existing.LeaseExpiresUtc > DateTime.UtcNow)
        {
            for (int i = 0; i < 12; i++)
            {
                await Task.Delay(250, ct);
                existing = await db.RequestDocumentDerivatives
                    .AsNoTracking()
                    .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);

                if (existing != null && existing.Status == DocumentDerivativeStatus.Ready && !string.IsNullOrEmpty(existing.StoragePath) && File.Exists(existing.StoragePath))
                {
                    return existing;
                }

                if (existing == null || existing.Status != DocumentDerivativeStatus.Pending || existing.LeaseExpiresUtc <= DateTime.UtcNow)
                {
                    break;
                }
            }
        }

        if (existing == null) return null;

        // Attempt atomic takeover of expired/failed lease
        var takeoverToken = Guid.NewGuid();
        var claimTime = DateTime.UtcNow;
        var existingLease = existing.LeaseToken;
        var existingId = existing.DerivativeId;

        var rowsClaimed = await db.RequestDocumentDerivatives
            .Where(d => d.DerivativeId == existingId
                     && d.LeaseToken == existingLease
                     && (d.LeaseExpiresUtc <= claimTime || d.Status == DocumentDerivativeStatus.Failed || d.SanitizerVersion != CurrentSanitizerVersion || d.RawFileHash != doc.FileHash))
            .ExecuteUpdateAsync(u => u
                .SetProperty(d => d.Status, DocumentDerivativeStatus.Pending)
                .SetProperty(d => d.LeaseToken, takeoverToken)
                .SetProperty(d => d.LeaseExpiresUtc, claimTime.AddMinutes(3))
                .SetProperty(d => d.RawFileHash, doc.FileHash ?? string.Empty)
                .SetProperty(d => d.SanitizerVersion, CurrentSanitizerVersion)
                .SetProperty(d => d.ErrorMessage, (string?)null)
                .SetProperty(d => d.UpdatedUtc, claimTime), ct);

        if (rowsClaimed == 0)
        {
            // Another worker won the lease race, reload latest state
            return await db.RequestDocumentDerivatives
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.DocumentId == doc.DocumentId && d.DerivativeType == DocumentDerivativeType.SanitizedExcel, ct);
        }

        // We hold the lease with takeoverToken on existingId; perform generation
        return await GenerateSanitizedDerivativeAsync(existingId, takeoverToken, doc, ext, ct);
    }

    private async Task<RequestDocumentDerivative?> GenerateSanitizedDerivativeAsync(
        long derivativeId,
        Guid workerToken,
        RequestDocument doc,
        string ext,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(doc.StoragePath) || !File.Exists(doc.StoragePath))
            {
                logger.LogWarning("Source document file not found at {StoragePath} for Document {DocumentId}", doc.StoragePath, doc.DocumentId);
                await db.RequestDocumentDerivatives
                    .Where(d => d.DerivativeId == derivativeId && d.LeaseToken == workerToken)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                        .SetProperty(d => d.ErrorMessage, "Source document file not found.")
                        .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                        .SetProperty(d => d.UpdatedUtc, DateTime.UtcNow), ct);

                return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
            }

            var derivativesDir = Path.Combine(env.ContentRootPath, "App_Data", "Uploads", doc.RequestId.ToString(), "derivatives");
            Directory.CreateDirectory(derivativesDir);

            var tempPath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized.{workerToken:N}{ext}");
            File.Copy(doc.StoragePath, tempPath, overwrite: true);

            ExcelMetadataSanitizer.SanitizeWorkbook(tempPath);

            var openCheck = fileValidation.ValidateOpens(tempPath);
            if (!openCheck.IsValid)
            {
                if (File.Exists(tempPath))
                {
                    try { File.Delete(tempPath); } catch { }
                }

                logger.LogWarning("Sanitized derivative failed validation for Document {DocumentId}: {Error}", doc.DocumentId, openCheck.Error);
                await db.RequestDocumentDerivatives
                    .Where(d => d.DerivativeId == derivativeId && d.LeaseToken == workerToken)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                        .SetProperty(d => d.ErrorMessage, openCheck.Error ?? "Validation failed.")
                        .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                        .SetProperty(d => d.UpdatedUtc, DateTime.UtcNow), ct);

                return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
            }

            var finalPath = Path.Combine(derivativesDir, $"{doc.DocumentId}_sanitized{ext}");
            File.Move(tempPath, finalPath, overwrite: true);

            var fileInfo = new FileInfo(finalPath);
            string fileHash;
            using (var fs = File.OpenRead(finalPath))
            {
                fileHash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
            }

            await db.RequestDocumentDerivatives
                .Where(d => d.DerivativeId == derivativeId && d.LeaseToken == workerToken)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DocumentDerivativeStatus.Ready)
                    .SetProperty(d => d.StoragePath, finalPath)
                    .SetProperty(d => d.FileSize, fileInfo.Length)
                    .SetProperty(d => d.FileHash, fileHash)
                    .SetProperty(d => d.ErrorMessage, (string?)null)
                    .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(d => d.UpdatedUtc, DateTime.UtcNow), ct);

            return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error generating derivative for Document {DocumentId}", doc.DocumentId);
            await db.RequestDocumentDerivatives
                .Where(d => d.DerivativeId == derivativeId && d.LeaseToken == workerToken)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DocumentDerivativeStatus.Failed)
                    .SetProperty(d => d.ErrorMessage, ex.Message)
                    .SetProperty(d => d.LeaseExpiresUtc, (DateTime?)null)
                    .SetProperty(d => d.UpdatedUtc, DateTime.UtcNow), ct);

            return await db.RequestDocumentDerivatives.AsNoTracking().FirstOrDefaultAsync(d => d.DerivativeId == derivativeId, ct);
        }
    }
}
