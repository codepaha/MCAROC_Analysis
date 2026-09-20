using System.IO.Compression;
using MCAROC_Analysis.Services.AutoFetch;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>One retained order file to include in the bulk ZIP: which case it belongs to (drives the folder
/// layout) and a display label for the entry itself, plus the file already on disk.</summary>
public sealed record LitigationOrderArchiveEntry(string CaseFolderLabel, string EntryLabel, string LocalPath);

/// <summary>Packages every currently-retained order PDF for one request into a single flat ZIP, grouped by
/// case folder — "one request-scoped bulk ZIP download while files remain retained" (epic #239). Reuses
/// <see cref="AutoFetchArchiveBuilder"/>'s folder/entry-name sanitization rather than duplicating them; unlike
/// that builder this produces one flat archive (orders have no nested-zip identity to preserve). Pure file
/// I/O, no DB — the caller is responsible for selecting which <c>LitigationOrderDocument</c> rows are
/// currently Downloaded and passing only those in.
///
/// Skips any entry whose file is no longer actually on disk rather than throwing — "ZIP contains every
/// currently retained order," never more, never less, and never a hard failure over one missing file (a
/// caller can compare the returned count against how many entries it passed in to detect this).</summary>
public static class LitigationOrdersArchiveBuilder
{
    public static int Build(string zipPath, IEnumerable<LitigationOrderArchiveEntry> entries, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
        using var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(entry.LocalPath)) continue;

            var folder = AutoFetchArchiveBuilder.SanitizeFolder(entry.CaseFolderLabel);
            var fileName = AutoFetchArchiveBuilder.SanitizeEntryName(entry.EntryLabel);
            var name = UniqueEntryName($"{folder}/{fileName}", usedNames);
            archive.CreateEntryFromFile(entry.LocalPath, name, CompressionLevel.Fastest);
            written++;
        }
        return written;
    }

    private static string UniqueEntryName(string name, HashSet<string> used)
    {
        if (used.Add(name)) return name;
        var extIndex = name.LastIndexOf('.');
        var stem = extIndex > 0 ? name[..extIndex] : name;
        var ext = extIndex > 0 ? name[extIndex..] : "";
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }
}
