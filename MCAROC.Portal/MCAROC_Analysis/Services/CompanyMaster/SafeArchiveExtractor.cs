using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public interface ISafeArchiveExtractor
{
    Task<IReadOnlyList<string>> ExtractSafelyAsync(Stream archiveStream, string destinationDirectory, CancellationToken cancellationToken = default);
}

public sealed class SafeArchiveExtractor : ISafeArchiveExtractor
{
    public const long MaxArchiveSizeBytes = 500L * 1024 * 1024; // 500 MB
    public const long MaxUncompressedSizeBytes = 5L * 1024 * 1024 * 1024; // 5 GB
    public const int MaxEntryCount = 50;
    public const double MaxCompressionRatio = 20.0;

    private readonly ILogger<SafeArchiveExtractor> _logger;

    public SafeArchiveExtractor(ILogger<SafeArchiveExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> ExtractSafelyAsync(Stream archiveStream, string destinationDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archiveStream);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);

        if (archiveStream.CanSeek && archiveStream.Length > MaxArchiveSizeBytes)
        {
            throw new InvalidOperationException($"Archive size {archiveStream.Length:N0} bytes exceeds maximum allowed limit of {MaxArchiveSizeBytes:N0} bytes (500 MB).");
        }

        Directory.CreateDirectory(destinationDirectory);
        string normalizedDestDir = Path.GetFullPath(destinationDirectory);
        if (!normalizedDestDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            normalizedDestDir += Path.DirectorySeparatorChar;
        }

        var extractedFiles = new List<string>();
        long totalUncompressedBytes = 0;
        int entryCount = 0;

        using var zip = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: true);

        if (zip.Entries.Count > MaxEntryCount)
        {
            throw new InvalidOperationException($"Archive contains {zip.Entries.Count} entries, exceeding maximum permitted count of {MaxEntryCount}.");
        }

        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entryCount++;

            // Path Traversal Defenses:
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                // directory entry
                continue;
            }

            if (entry.FullName.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Malicious path traversal detected in archive entry: '{entry.FullName}'");
            }

            string targetPath = Path.GetFullPath(Path.Combine(normalizedDestDir, entry.FullName));
            if (!targetPath.StartsWith(normalizedDestDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' attempts to extract outside destination directory '{normalizedDestDir}'.");
            }

            // Zip Bomb Defenses:
            long entryUncompressed = entry.Length;
            long entryCompressed = entry.CompressedLength;

            if (entryCompressed > 0)
            {
                double ratio = (double)entryUncompressed / entryCompressed;
                if (ratio > MaxCompressionRatio && entryUncompressed > 10 * 1024 * 1024)
                {
                    throw new InvalidOperationException($"High compression ratio ({ratio:F1}:1) detected for '{entry.FullName}'. Possible zip bomb.");
                }
            }

            totalUncompressedBytes += entryUncompressed;
            if (totalUncompressedBytes > MaxUncompressedSizeBytes)
            {
                throw new InvalidOperationException($"Total uncompressed archive size exceeds safety threshold of {MaxUncompressedSizeBytes:N0} bytes (5 GB).");
            }

            string? targetParent = Path.GetDirectoryName(targetPath);
            if (targetParent != null)
            {
                Directory.CreateDirectory(targetParent);
            }

            await using (var entryStream = entry.Open())
            await using (var targetFileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await entryStream.CopyToAsync(targetFileStream, cancellationToken);
            }

            extractedFiles.Add(targetPath);
        }

        _logger.LogInformation("Safely extracted {Count} files ({Bytes:N0} bytes) into {Dir}",
            extractedFiles.Count, totalUncompressedBytes, destinationDirectory);

        return extractedFiles;
    }
}
