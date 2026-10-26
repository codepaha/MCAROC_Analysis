namespace MCAROC_Analysis.Services.Storage;

using System.Diagnostics;
using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

public sealed record StoragePathBackfillReport(
    int RowsScanned,
    int RowsRewritten,
    Dictionary<string, int> RewrittenByEntity,
    IReadOnlyList<UnrewrittenPathInfo> UnrewrittenPaths);

public sealed record UnrewrittenPathInfo(
    string EntityName,
    string PrimaryKey,
    string PropertyName,
    string Path);

public sealed class StoragePathBackfillService(
    AppDbContext db,
    IStoragePathResolver resolver,
    ILogger<StoragePathBackfillService> logger)
{
    public async Task<StoragePathBackfillReport> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var totalScanned = 0;
        var totalRewritten = 0;
        var rewrittenByEntity = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var unrewritten = new List<UnrewrittenPathInfo>();

        // 1. LitigationOrderDocuments
        var (lodScanned, lodRewritten) = await BackfillLitigationOrderDocumentsAsync(unrewritten, ct);
        totalScanned += lodScanned;
        totalRewritten += lodRewritten;
        rewrittenByEntity["LitigationOrderDocuments"] = lodRewritten;

        // 2. McaFilingDocuments
        var (mfdScanned, mfdRewritten) = await BackfillMcaFilingDocumentsAsync(unrewritten, ct);
        totalScanned += mfdScanned;
        totalRewritten += mfdRewritten;
        rewrittenByEntity["McaFilingDocuments"] = mfdRewritten;

        // 3. PreLoginReportJobs
        var (plrScanned, plrRewritten) = await BackfillPreLoginReportJobsAsync(unrewritten, ct);
        totalScanned += plrScanned;
        totalRewritten += plrRewritten;
        rewrittenByEntity["PreLoginReportJobs"] = plrRewritten;

        // 4. RequestDocuments
        var (rdScanned, rdRewritten) = await BackfillRequestDocumentsAsync(unrewritten, ct);
        totalScanned += rdScanned;
        totalRewritten += rdRewritten;
        rewrittenByEntity["RequestDocuments"] = rdRewritten;

        // 5. RequestDocumentDerivatives
        var (rddScanned, rddRewritten) = await BackfillRequestDocumentDerivativesAsync(unrewritten, ct);
        totalScanned += rddScanned;
        totalRewritten += rddRewritten;
        rewrittenByEntity["RequestDocumentDerivatives"] = rddRewritten;

        // 6. LargeArchiveUploadSessions
        var (lausScanned, lausRewritten) = await BackfillLargeArchiveUploadSessionsAsync(unrewritten, ct);
        totalScanned += lausScanned;
        totalRewritten += lausRewritten;
        rewrittenByEntity["LargeArchiveUploadSessions"] = lausRewritten;

        sw.Stop();
        logger.LogInformation(
            "StoragePathBackfill completed in {ElapsedMs}ms: Scanned {TotalScanned}, Rewritten {TotalRewritten}, Unrewritten outside App_Data {UnrewrittenCount}",
            sw.ElapsedMilliseconds, totalScanned, totalRewritten, unrewritten.Count);

        if (unrewritten.Count > 0)
        {
            foreach (var item in unrewritten.Take(20))
            {
                logger.LogWarning(
                    "StoragePathBackfill: Path outside App_Data left unchanged: Entity={Entity}, Id={Id}, Property={Property}, Path={Path}",
                    item.EntityName, item.PrimaryKey, item.PropertyName, item.Path);
            }
        }

        return new StoragePathBackfillReport(totalScanned, totalRewritten, rewrittenByEntity, unrewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillLitigationOrderDocumentsAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var docs = await db.LitigationOrderDocuments
            .Where(d => d.StoragePath != null && d.StoragePath != "")
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc.StoragePath)) continue;

            if (ShouldRewrite(doc.StoragePath, out var relativePath))
            {
                doc.StoragePath = relativePath;
                rewritten++;
            }
            else if (IsUnrewrittenExternal(doc.StoragePath))
            {
                unrewritten.Add(new UnrewrittenPathInfo("LitigationOrderDocuments", doc.LitigationOrderDocumentId.ToString(), "StoragePath", doc.StoragePath));
            }
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (docs.Count, rewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillMcaFilingDocumentsAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var docs = await db.McaFilingDocuments
            .Where(d => (d.StoragePath != null && d.StoragePath != "") || (d.ExtractedTextPath != null && d.ExtractedTextPath != ""))
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var doc in docs)
        {
            var modified = false;
            if (!string.IsNullOrWhiteSpace(doc.StoragePath))
            {
                if (ShouldRewrite(doc.StoragePath, out var relStorage))
                {
                    doc.StoragePath = relStorage;
                    modified = true;
                }
                else if (IsUnrewrittenExternal(doc.StoragePath))
                {
                    unrewritten.Add(new UnrewrittenPathInfo("McaFilingDocuments", doc.FilingDocumentId.ToString(), "StoragePath", doc.StoragePath));
                }
            }

            if (!string.IsNullOrWhiteSpace(doc.ExtractedTextPath))
            {
                if (ShouldRewrite(doc.ExtractedTextPath, out var relText))
                {
                    doc.ExtractedTextPath = relText;
                    modified = true;
                }
                else if (IsUnrewrittenExternal(doc.ExtractedTextPath))
                {
                    unrewritten.Add(new UnrewrittenPathInfo("McaFilingDocuments", doc.FilingDocumentId.ToString(), "ExtractedTextPath", doc.ExtractedTextPath));
                }
            }

            if (modified)
                rewritten++;
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (docs.Count, rewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillPreLoginReportJobsAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var jobs = await db.PreLoginReportJobs
            .Where(j => j.ReportStoragePath != null && j.ReportStoragePath != "")
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var job in jobs)
        {
            if (string.IsNullOrWhiteSpace(job.ReportStoragePath)) continue;

            if (ShouldRewrite(job.ReportStoragePath, out var relativePath))
            {
                job.ReportStoragePath = relativePath;
                rewritten++;
            }
            else if (IsUnrewrittenExternal(job.ReportStoragePath))
            {
                unrewritten.Add(new UnrewrittenPathInfo("PreLoginReportJobs", job.PreLoginReportJobId.ToString(), "ReportStoragePath", job.ReportStoragePath));
            }
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (jobs.Count, rewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillRequestDocumentsAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var docs = await db.RequestDocuments
            .Where(d => d.StoragePath != null && d.StoragePath != "")
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var doc in docs)
        {
            if (string.IsNullOrWhiteSpace(doc.StoragePath)) continue;

            if (ShouldRewrite(doc.StoragePath, out var relativePath))
            {
                doc.StoragePath = relativePath;
                rewritten++;
            }
            else if (IsUnrewrittenExternal(doc.StoragePath))
            {
                unrewritten.Add(new UnrewrittenPathInfo("RequestDocuments", doc.DocumentId.ToString(), "StoragePath", doc.StoragePath));
            }
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (docs.Count, rewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillRequestDocumentDerivativesAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var derivatives = await db.RequestDocumentDerivatives
            .Where(d => d.StoragePath != null && d.StoragePath != "")
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var d in derivatives)
        {
            if (string.IsNullOrWhiteSpace(d.StoragePath)) continue;

            if (ShouldRewrite(d.StoragePath, out var relativePath))
            {
                d.StoragePath = relativePath;
                rewritten++;
            }
            else if (IsUnrewrittenExternal(d.StoragePath))
            {
                unrewritten.Add(new UnrewrittenPathInfo("RequestDocumentDerivatives", d.DerivativeId.ToString(), "StoragePath", d.StoragePath));
            }
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (derivatives.Count, rewritten);
    }

    private async Task<(int scanned, int rewritten)> BackfillLargeArchiveUploadSessionsAsync(List<UnrewrittenPathInfo> unrewritten, CancellationToken ct)
    {
        var sessions = await db.LargeArchiveUploadSessions
            .Where(s => (s.DestinationStoragePath != null && s.DestinationStoragePath != "") || (s.StagingFilePath != null && s.StagingFilePath != ""))
            .ToListAsync(ct);

        var rewritten = 0;
        foreach (var s in sessions)
        {
            var modified = false;
            if (!string.IsNullOrWhiteSpace(s.DestinationStoragePath))
            {
                if (ShouldRewrite(s.DestinationStoragePath, out var relDest))
                {
                    s.DestinationStoragePath = relDest;
                    modified = true;
                }
                else if (IsUnrewrittenExternal(s.DestinationStoragePath))
                {
                    unrewritten.Add(new UnrewrittenPathInfo("LargeArchiveUploadSessions", s.SessionId.ToString(), "DestinationStoragePath", s.DestinationStoragePath));
                }
            }

            if (!string.IsNullOrWhiteSpace(s.StagingFilePath))
            {
                if (ShouldRewrite(s.StagingFilePath, out var relStaging))
                {
                    s.StagingFilePath = relStaging;
                    modified = true;
                }
                else if (IsUnrewrittenExternal(s.StagingFilePath))
                {
                    unrewritten.Add(new UnrewrittenPathInfo("LargeArchiveUploadSessions", s.SessionId.ToString(), "StagingFilePath", s.StagingFilePath));
                }
            }

            if (modified)
                rewritten++;
        }

        if (rewritten > 0)
            await db.SaveChangesAsync(ct);

        return (sessions.Count, rewritten);
    }

    private bool ShouldRewrite(string path, out string relativePath)
    {
        relativePath = resolver.ToRelativePath(path);
        // If ToRelativePath changed the path (e.g. stripped App_Data or data root)
        return !string.Equals(path, relativePath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnrewrittenExternal(string path)
    {
        // An unrewritten path is rooted/absolute and does not contain App_Data
        return Path.IsPathRooted(path) &&
               path.IndexOf("app_data", StringComparison.OrdinalIgnoreCase) < 0;
    }
}
