using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>One file to package inside a nested zip, with its optional reference tool source key info.</summary>
public sealed record ArchiveFile(string EntryName, string LocalPath, string? AwsPath = null, string? Did = null, string? AttachmentName = null);

/// <summary>One filing to package: its outer section folder, the nested-zip identity, and the PDFs
/// (already on disk) that go inside it.</summary>
public sealed record ArchiveFiling(string SectionFolder, string DocId, IReadOnlyList<ArchiveFile> Files)
{
    // Backwards-compatible constructor for existing tests and call sites passing (EntryName, LocalPath) tuples
    public ArchiveFiling(string sectionFolder, string docId, IReadOnlyList<(string EntryName, string LocalPath)> files)
        : this(sectionFolder, docId, files.Select(f => new ArchiveFile(f.EntryName, f.LocalPath)).ToList())
    {
    }
}

/// <summary>Packages downloaded filing PDFs into exactly the archive layout <c>FilingBatchProcessor</c>
/// unpacks: an outer zip of <c>{Section folder}/{docId}_{COMPANY}_{CIN}.zip</c> nested zips, each
/// holding one filing's main e-form PDF plus its attachments. The nested-zip name is what
/// <c>FilingIdentityParser</c> reads the SRN-equivalent (here the tool's document id), company and CIN
/// from, and the section folder is what <c>FilingClassifier</c> falls back to for generic filenames.
/// Pure file I/O, no DB.</summary>
public static partial class AutoFetchArchiveBuilder
{
    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1F]+")]
    private static partial Regex InvalidEntryChars();

    /// <summary>Builds the outer archive at <paramref name="outerZipPath"/>. Nested zips are stored
    /// uncompressed inside the outer zip (they are already deflated) so the outer write is one pass.</summary>
    public static void Build(string outerZipPath, string companyName, string cin, IEnumerable<ArchiveFiling> filings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outerZipPath)!);
        var companyToken = CompanyToken(companyName);
        var cinToken = cin.Trim().ToUpperInvariant();

        using var outerStream = new FileStream(outerZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var outer = new ZipArchive(outerStream, ZipArchiveMode.Create, leaveOpen: false);
        foreach (var filing in filings)
        {
            ct.ThrowIfCancellationRequested();
            var present = filing.Files.Where(f => File.Exists(f.LocalPath)).ToList();
            if (present.Count == 0) continue;

            var nestedName = $"{filing.DocId}_{companyToken}_{cinToken}.zip";
            var entry = outer.CreateEntry($"{SanitizeFolder(filing.SectionFolder)}/{nestedName}", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            using var nested = new ZipArchive(entryStream, ZipArchiveMode.Create, leaveOpen: true);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var manifestEntries = new List<ArchiveManifestFileEntry>();

            foreach (var file in present)
            {
                var name = UniqueEntryName(SanitizeEntryName(file.EntryName), usedNames);
                nested.CreateEntryFromFile(file.LocalPath, name, CompressionLevel.Fastest);
                if (!string.IsNullOrEmpty(file.AwsPath) || !string.IsNullOrEmpty(file.Did))
                {
                    manifestEntries.Add(new ArchiveManifestFileEntry(name, filing.DocId, file.AwsPath, file.AttachmentName ?? file.EntryName));
                }
            }

            if (manifestEntries.Count > 0)
            {
                var manifestEntry = nested.CreateEntry("manifest.json", CompressionLevel.Fastest);
                using var ms = manifestEntry.Open();
                System.Text.Json.JsonSerializer.Serialize(ms, manifestEntries);
            }
        }
    }

    public sealed record ArchiveManifestFileEntry(string EntryName, string SourceDocId, string? SourceAwsPath, string? SourceAttachmentName);

    /// <summary>"Lodha Developers Limited" → "LODHA_DEVELOPERS_LIMITED" (what FilingIdentityParser turns
    /// back into "LODHA DEVELOPERS LIMITED"). Capped so a long name can't push the nested-zip filename
    /// past sensible path limits once the request/batch folders are prepended.</summary>
    public static string CompanyToken(string companyName)
    {
        var token = NonAlphanumeric().Replace(companyName.ToUpperInvariant(), "_").Trim('_');
        if (token.Length == 0) token = "COMPANY";
        return token.Length > 60 ? token[..60].TrimEnd('_') : token;
    }

    public static string SanitizeFolder(string folder)
    {
        var cleaned = InvalidEntryChars().Replace(folder, " ").Trim();
        return cleaned.Length == 0 ? "Documents" : cleaned;
    }

    /// <summary>Keeps the original name (the classifier keys on words like "CHG-1" in it) but strips
    /// path separators and control characters, guarantees a .pdf extension, and caps the length.</summary>
    public static string SanitizeEntryName(string name)
    {
        var cleaned = InvalidEntryChars().Replace(name, " ").Trim(' ', '.', '	');
        if (cleaned.Length == 0) cleaned = "document";
        if (!cleaned.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) cleaned += ".pdf";
        if (cleaned.Length > 150)
            cleaned = cleaned[..(150 - 4)].TrimEnd() + ".pdf";
        return cleaned;
    }

    private static string UniqueEntryName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var stem = name[..^4];
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}).pdf";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Filesystem-safe folder name for one staged filing (the doc id is already hex, this only
    /// guards against a surprising value).</summary>
    public static string SafeDocFolder(string docId)
    {
        var sb = new StringBuilder(docId.Length);
        foreach (var c in docId)
            sb.Append(char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_');
        return sb.Length == 0 ? "doc" : sb.ToString();
    }
}
