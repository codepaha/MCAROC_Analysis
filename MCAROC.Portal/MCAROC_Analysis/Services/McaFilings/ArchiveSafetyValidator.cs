using System.IO.Compression;

namespace MCAROC_Analysis.Services.McaFilings;

public record ArchiveSafetyResult(bool IsValid, string? Error);

public record ArchiveSafetyLimits(
    long MaxArchiveSizeBytes = 2_000_000_000, // 2 GB — the real sample corpus is ~700MB
    long MaxUncompressedSizeBytes = 8_000_000_000, // guards zip bombs: BATCH-WIDE cumulative uncompressed total (see CumulativeArchiveStats)
    int MaxNestedDepth = 2, // outer -> nested; nothing deeper is expected or allowed
    int MaxPdfCount = 5000, // also batch-wide cumulative
    long MaxIndividualPdfSizeBytes = 200_000_000)
{
    public static readonly ArchiveSafetyLimits Default = new();
}

/// <summary>Accumulates uncompressed-size and PDF-count totals across every nested zip processed while
/// unpacking one batch. A single instance must be shared across all ValidateNestedArchive calls for the
/// same batch — checking each nested zip's declared sizes in isolation is not sufficient, because many
/// individually-small-but-highly-compressed nested zips can still combine to exhaust disk even though none
/// of them alone exceeds the limit. Not thread-safe by design: batch unpacking processes nested zips
/// sequentially (see FilingBatchProcessor.UnpackBatchAsync), so no locking is needed here.
///
/// Known residual gap: on a resumed run after a crash (RecoverStaleWorkAsync re-enqueues an
/// Unpacking/Indexing-stuck batch), a fresh tracker starts at zero rather than accounting for nested zips
/// already unpacked before the crash — so the cumulative cap is under-enforced across a crash+resume
/// sequence. Closing that fully would mean persisting running totals per batch (or re-deriving them from
/// already-extracted files on disk) rather than just in memory; not done here since it requires an
/// attacker to also trigger a crash mid-unpack to exploit, not a normal-path gap.</summary>
public class CumulativeArchiveStats
{
    public long UncompressedBytes { get; private set; }
    public int PdfCount { get; private set; }

    public void Add(long uncompressedBytes, bool isPdf)
    {
        UncompressedBytes += uncompressedBytes;
        if (isPdf) PdfCount++;
    }
}

/// <summary>Validates an archive is safe to unpack before touching it: size/depth/count limits (zip-bomb
/// guard) and zip-slip protection (every entry's resolved path must stay inside the extraction directory).
/// Applied at every archive level — the outer zip and each nested per-filing zip.</summary>
public static class ArchiveSafetyValidator
{
    public static ArchiveSafetyResult ValidateOuterArchive(string zipPath, ArchiveSafetyLimits limits)
    {
        var fileInfo = new FileInfo(zipPath);
        if (!fileInfo.Exists)
            return new ArchiveSafetyResult(false, "Archive file not found.");
        if (fileInfo.Length > limits.MaxArchiveSizeBytes)
            return new ArchiveSafetyResult(false, $"Archive exceeds the {limits.MaxArchiveSizeBytes / 1_000_000} MB limit.");

        // The outer zip's own entries are just the nested zip files as opaque blobs — their declared
        // "Length" here is the nested zip's own size, not what's uncompressed inside it. That recursive
        // total is only knowable by descending into each nested zip, which happens via
        // ValidateNestedArchive against a shared CumulativeArchiveStats — this call intentionally does NOT
        // check cumulative bytes/PDF count itself, only the outer archive's own declared size and paths.
        return ValidateEntries(zipPath, limits, stats: null);
    }

    /// <summary>Validates a nested zip's raw bytes (already extracted from the outer archive to a temp
    /// location). <paramref name="stats"/> must be the same instance across every nested zip in one
    /// batch's unpack operation, so the uncompressed-size and PDF-count limits are enforced cumulatively,
    /// not per-nested-zip.</summary>
    public static ArchiveSafetyResult ValidateNestedArchive(string zipPath, ArchiveSafetyLimits limits, int currentDepth, CumulativeArchiveStats stats)
    {
        if (currentDepth > limits.MaxNestedDepth)
            return new ArchiveSafetyResult(false, $"Archive nesting exceeds the maximum depth of {limits.MaxNestedDepth}.");

        return ValidateEntries(zipPath, limits, stats);
    }

    private static ArchiveSafetyResult ValidateEntries(string zipPath, ArchiveSafetyLimits limits, CumulativeArchiveStats? stats)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
                continue; // directory marker

            if (!IsPathSafe(entry.FullName))
                return new ArchiveSafetyResult(false, $"Rejected entry with an unsafe path: '{entry.FullName}'.");

            var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
            var isPdf = ext == ".pdf";

            if (stats is not null)
            {
                stats.Add(entry.Length, isPdf);
                if (stats.UncompressedBytes > limits.MaxUncompressedSizeBytes)
                    return new ArchiveSafetyResult(false, "Archive's cumulative uncompressed size across the batch exceeds the configured limit (possible zip bomb).");
                if (stats.PdfCount > limits.MaxPdfCount)
                    return new ArchiveSafetyResult(false, $"Archive contains more than the {limits.MaxPdfCount} cumulative PDF limit.");
            }

            if (isPdf && entry.Length > limits.MaxIndividualPdfSizeBytes)
                return new ArchiveSafetyResult(false, $"PDF entry '{entry.FullName}' exceeds the per-file size limit.");
        }

        return new ArchiveSafetyResult(true, null);
    }

    /// <summary>Zip-slip guard: an entry's name must not escape the extraction directory once resolved.
    /// Rejects absolute paths, drive-letter paths, and any ".." traversal segment.</summary>
    public static bool IsPathSafe(string entryFullName)
    {
        if (string.IsNullOrWhiteSpace(entryFullName)) return false;
        if (Path.IsPathRooted(entryFullName)) return false;

        var normalized = entryFullName.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.All(s => s != "..");
    }

    /// <summary>Resolves an entry's safe extraction path, throwing if it would escape <paramref name="targetDirectory"/>.
    /// Use this (not raw path concatenation) whenever actually writing an entry to disk.</summary>
    public static string ResolveSafeExtractionPath(string targetDirectory, string entryFullName)
    {
        var fullTargetDir = Path.GetFullPath(targetDirectory);
        var candidate = Path.GetFullPath(Path.Combine(fullTargetDir, entryFullName));
        if (!candidate.StartsWith(fullTargetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !candidate.Equals(fullTargetDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Zip entry '{entryFullName}' resolves outside the extraction directory.");
        }
        return candidate;
    }
}
