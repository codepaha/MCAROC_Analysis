// Dev-only utility: scans a scraped litigation-order corpus (one or more ZIP archives, each holding
// "<case folder>/<order file>" entries) for files that share a parent folder but are byte-identical to
// a sibling under a different name. That pattern isn't a legitimate duplicate — it's the signature of a
// scraper bug that re-saved the same order PDF under several different order-number/date filenames,
// silently losing the real content for every date but one. Never commits/reads the proprietary corpus
// into the repo — the path is supplied as an argument, pointing at wherever it lives on disk locally.
//
// Usage:
//   dotnet run --project Tools/LitigationCorpusDedupeScanner -- "<folder containing .zip files>"
//   dotnet run --project Tools/LitigationCorpusDedupeScanner -- "<path to a single .zip file>"
//
// Exit code 0 = no same-folder duplicate content found. Exit code 1 = at least one cluster found (or
// the path was invalid) — treat that as a gate: do not embed the corpus until it's resolved.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

var rootPath = args.Length > 0 ? args[0] : "";
if (rootPath.Length == 0 || (!Directory.Exists(rootPath) && !File.Exists(rootPath)))
{
    Console.Error.WriteLine($"Path not found: {rootPath}");
    Console.Error.WriteLine("Usage: dotnet run --project Tools/LitigationCorpusDedupeScanner -- <folder-of-zips-or-a-single-zip>");
    return 1;
}

var zipPaths = File.Exists(rootPath) && rootPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
    ? [rootPath]
    : Directory.GetFiles(rootPath, "*.zip", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal).ToArray();

if (zipPaths.Length == 0)
{
    Console.Error.WriteLine($"No .zip files found at: {rootPath}");
    return 1;
}

var allClusters = new List<DuplicateCluster>();
List<string[]> summaryRows = [["Archive", "Total files", "Distinct-content files", "Redundant (duplicate) files", "Affected folders"]];

foreach (var zipPath in zipPaths)
{
    var zipName = Path.GetFileName(zipPath);
    using var archive = ZipFile.OpenRead(zipPath);

    // (folder, sha256) -> entry full names sharing that hash within that folder
    var groups = new Dictionary<(string Folder, string Sha256), List<string>>();
    var totalFiles = 0;

    foreach (var entry in archive.Entries)
    {
        if (entry.FullName.EndsWith('/') || entry.Length == 0) continue; // directory marker, not a real file
        totalFiles++;

        var lastSlash = entry.FullName.LastIndexOf('/');
        var folder = lastSlash < 0 ? "" : entry.FullName[..lastSlash];

        using var stream = entry.Open();
        using var sha256 = SHA256.Create();
        var hash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();

        var key = (folder, hash);
        if (!groups.TryGetValue(key, out var names))
            groups[key] = names = [];
        names.Add(entry.FullName);
    }

    var clusters = groups.Where(g => g.Value.Count > 1)
        .Select(g => new DuplicateCluster(zipName, g.Key.Folder, g.Key.Sha256, g.Value.OrderBy(n => n, StringComparer.Ordinal).ToList()))
        .OrderBy(c => c.Folder, StringComparer.Ordinal)
        .ToList();
    allClusters.AddRange(clusters);

    var redundant = clusters.Sum(c => c.Files.Count - 1);
    var affectedFolders = clusters.Select(c => c.Folder).Distinct().Count();

    summaryRows.Add([
        zipName,
        totalFiles.ToString(),
        (totalFiles - redundant).ToString(),
        redundant.ToString(),
        affectedFolders.ToString()
    ]);
}

var widths = Enumerable.Range(0, summaryRows[0].Length).Select(i => summaryRows.Max(r => r[i].Length)).ToArray();
foreach (var row in summaryRows)
    Console.WriteLine(string.Join(" | ", row.Select((cell, i) => cell.PadRight(widths[i]))));

if (allClusters.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("================================================================================");
    Console.WriteLine("DUPLICATE-CONTENT CLUSTERS (same folder, byte-identical file under a different name)");
    Console.WriteLine("================================================================================");
    foreach (var c in allClusters)
    {
        Console.WriteLine($"\n[{c.Archive}] {c.Folder}  ({c.Files.Count} files share sha256 {c.Sha256[..12]}...)");
        foreach (var f in c.Files) Console.WriteLine($"    {Path.GetFileName(f)}");
    }

    var jsonPath = Path.Combine(AppContext.BaseDirectory, "duplicate-content-report.json");
    File.WriteAllText(jsonPath, JsonSerializer.Serialize(allClusters, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\nFull cluster report written to: {jsonPath}");

    var totalRedundant = allClusters.Sum(c => c.Files.Count - 1);
    Console.WriteLine();
    Console.WriteLine($"{allClusters.Count} cluster(s) across {allClusters.Select(c => c.Archive).Distinct().Count()} archive(s), " +
                       $"{totalRedundant} redundant file(s) whose real content is missing from the corpus.");
    Console.WriteLine("Do not embed the affected archive(s) until these case folders are re-sourced.");
    return 1;
}

Console.WriteLine();
Console.WriteLine("No same-folder duplicate content found. Safe to proceed with embedding.");
return 0;

record DuplicateCluster(string Archive, string Folder, string Sha256, List<string> Files);
