using System.IO.Compression;
using System.Security.Cryptography;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Stream-only inventory of a Coastal Projects Limited outer ZIP archive.
/// No database calls, no file writes, no static state. Caller retains
/// ownership of <paramref name="outerZipStream"/> (<c>leaveOpen: true</c>).
/// <para>
/// WARNING: This public Stream API performs no archive-size or entry-count
/// limits. It must not be used on user-uploaded archives without adding
/// appropriate safety limits first.
/// </para>
/// </summary>
public static class CoastalCorpusInventoryService
{
    public static CoastalInventoryResult BuildManifest(Stream outerZipStream)
    {
        ArgumentNullException.ThrowIfNull(outerZipStream);

        // Phase 1: Traversal and hashing
        var nestedArchiveEntries = new List<string>();
        var rawEntries = new List<(string OuterFullPath, string InnerRelativePath, long UncompressedLength, string Sha256Hex)>();

        using (var outerArchive = new ZipArchive(outerZipStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            var zipEntries = outerArchive.Entries
                .Where(e => e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var outerEntry in zipEntries)
            {
                nestedArchiveEntries.Add(outerEntry.FullName);

                using var nestedStream = outerEntry.Open();
                using var nestedArchive = new ZipArchive(nestedStream, ZipArchiveMode.Read, leaveOpen: false);

                var pdfEntries = nestedArchive.Entries
                    .Where(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName, StringComparer.Ordinal)
                    .ToList();

                foreach (var pdfEntry in pdfEntries)
                {
                    string sha256Hex;
                    using (var pdfStream = pdfEntry.Open())
                    {
                        using var sha256 = SHA256.Create();
                        var hashBytes = sha256.ComputeHash(pdfStream);
                        sha256Hex = Convert.ToHexString(hashBytes).ToLowerInvariant();
                    }

                    rawEntries.Add((outerEntry.FullName, pdfEntry.FullName, pdfEntry.Length, sha256Hex));
                }
            }
        }

        // Phase 2: Canonical map and record construction
        var canonicalMap = new Dictionary<string, (string OuterFullPath, string InnerRelativePath)>(StringComparer.Ordinal);
        foreach (var (outerPath, innerPath, _, hash) in rawEntries)
        {
            if (!canonicalMap.ContainsKey(hash))
            {
                canonicalMap[hash] = (outerPath, innerPath);
            }
        }

        var manifestEntries = new List<CoastalManifestEntry>(rawEntries.Count);
        foreach (var (outerPath, innerPath, length, hash) in rawEntries)
        {
            var (canonOuter, canonInner) = canonicalMap[hash];
            bool isCanonical = string.Equals(outerPath, canonOuter, StringComparison.Ordinal) &&
                               string.Equals(innerPath, canonInner, StringComparison.Ordinal);

            manifestEntries.Add(new CoastalManifestEntry
            {
                OuterEntryFullPath = outerPath,
                NestedZipFileName = Path.GetFileName(outerPath),
                NestedEntryRelativePath = innerPath,
                UncompressedByteLength = length,
                Sha256Hex = hash,
                IsCanonical = isCanonical,
                CanonicalOuterEntryFullPath = canonOuter,
                CanonicalNestedEntryRelativePath = canonInner,
                CanonicalSha256Hex = hash
            });
        }

        return new CoastalInventoryResult(nestedArchiveEntries.AsReadOnly(), manifestEntries.AsReadOnly());
    }
}
