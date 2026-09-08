using System.IO.Compression;

namespace MCAROC_Analysis.Services.McaFilings;

public record ArchiveSafetyResult(bool IsValid, string? Error);

public record ArchiveSafetyLimits(
    long MaxArchiveSizeBytes = 2_000_000_000, // 2 GB — the real sample corpus is ~700MB
    long MaxUncompressedSizeBytes = 8_000_000_000, // guards zip bombs: declared uncompressed size, not just compressed
    int MaxNestedDepth = 2, // outer -> nested; nothing deeper is expected or allowed
    int MaxPdfCount = 5000,
    long MaxIndividualPdfSizeBytes = 200_000_000)
{
    public static readonly ArchiveSafetyLimits Default = new();
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

        return ValidateEntries(zipPath, limits, currentDepth: 1);
    }

    /// <summary>Validates a nested zip's raw bytes (already extracted from the outer archive to a temp
    /// location, or read via ZipArchive.Open on a stream — caller decides).</summary>
    public static ArchiveSafetyResult ValidateNestedArchive(string zipPath, ArchiveSafetyLimits limits, int currentDepth)
    {
        if (currentDepth > limits.MaxNestedDepth)
            return new ArchiveSafetyResult(false, $"Archive nesting exceeds the maximum depth of {limits.MaxNestedDepth}.");

        return ValidateEntries(zipPath, limits, currentDepth);
    }

    private static ArchiveSafetyResult ValidateEntries(string zipPath, ArchiveSafetyLimits limits, int currentDepth)
    {
        using var archive = ZipFile.OpenRead(zipPath);

        long cumulativeUncompressed = 0;
        var pdfCount = 0;

        foreach (var entry in archive.Entries)
        {
            if (entry.Length == 0 && entry.FullName.EndsWith('/'))
                continue; // directory marker

            if (!IsPathSafe(entry.FullName))
                return new ArchiveSafetyResult(false, $"Rejected entry with an unsafe path: '{entry.FullName}'.");

            cumulativeUncompressed += entry.Length;
            if (cumulativeUncompressed > limits.MaxUncompressedSizeBytes)
                return new ArchiveSafetyResult(false, "Archive's total uncompressed size exceeds the configured limit (possible zip bomb).");

            var ext = Path.GetExtension(entry.FullName).ToLowerInvariant();
            if (ext == ".pdf")
            {
                pdfCount++;
                if (entry.Length > limits.MaxIndividualPdfSizeBytes)
                    return new ArchiveSafetyResult(false, $"PDF entry '{entry.FullName}' exceeds the per-file size limit.");
                if (pdfCount > limits.MaxPdfCount)
                    return new ArchiveSafetyResult(false, $"Archive contains more than the {limits.MaxPdfCount} PDF limit.");
            }
            else if (ext != ".zip")
            {
                // Not a PDF or a nested zip we expect to descend into — skipped, not rejected outright;
                // the caller logs this against the batch/filing as a skipped-entry note.
            }
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
