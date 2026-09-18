using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>One filing to package: its outer section folder, the nested-zip identity, and the PDFs
/// (already on disk) that go inside it. <see cref="Files"/> maps the entry name inside the nested zip to
/// the local path.</summary>
public sealed record ArchiveFiling(string SectionFolder, string DocId, IReadOnlyList<(string EntryName, string LocalPath)> Files);

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
            foreach (var (entryName, localPath) in present)
            {
                var name = UniqueEntryName(SanitizeEntryName(entryName), usedNames);
                nested.CreateEntryFromFile(localPath, name, CompressionLevel.Fastest);
            }
        }
    }

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
