using System.IO.Compression;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

/// <summary>Covers LitigationOrdersArchiveBuilder's packaging contract (#243/LIT-03): "ZIP contains every
/// currently retained order" — never more (a missing file is silently skipped, never a hard failure), never
/// less (every present file makes it in, with no name collisions).</summary>
public sealed class LitigationOrdersArchiveBuilderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "litigation-archive-tests-" + Guid.NewGuid().ToString("N"));

    public LitigationOrdersArchiveBuilderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string WritePdf(string fileName, string content = "pdf-bytes")
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Build_includes_every_entry_whose_file_exists_grouped_by_case_folder()
    {
        var order1 = WritePdf("o1.pdf", "order one bytes");
        var order2 = WritePdf("o2.pdf", "order two bytes");
        var entries = new[]
        {
            new LitigationOrderArchiveEntry("Case 1/2020", "Judgment_09-01-2025_1", order1),
            new LitigationOrderArchiveEntry("Case 2/2020", "Order_10-01-2025_2", order2)
        };
        var zipPath = Path.Combine(_tempDir, "out.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries);

        Assert.Equal(2, written);
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Equal(2, archive.Entries.Count);
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("Case 1", StringComparison.Ordinal) && e.FullName.EndsWith(".pdf"));
        Assert.Contains(archive.Entries, e => e.FullName.StartsWith("Case 2", StringComparison.Ordinal) && e.FullName.EndsWith(".pdf"));
    }

    [Fact]
    public void Build_skips_an_entry_whose_file_no_longer_exists_on_disk_without_throwing()
    {
        var present = WritePdf("present.pdf");
        var missing = Path.Combine(_tempDir, "gone.pdf"); // never written
        var entries = new[]
        {
            new LitigationOrderArchiveEntry("Case 1", "present", present),
            new LitigationOrderArchiveEntry("Case 1", "missing", missing)
        };
        var zipPath = Path.Combine(_tempDir, "out.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries);

        Assert.Equal(1, written); // one silently skipped, not a hard failure
        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Single(archive.Entries);
    }

    [Fact]
    public void Build_disambiguates_two_entries_that_would_otherwise_collide_on_the_same_name()
    {
        var order1 = WritePdf("o1.pdf");
        var order2 = WritePdf("o2.pdf");
        var entries = new[]
        {
            new LitigationOrderArchiveEntry("Case 1", "Order", order1),
            new LitigationOrderArchiveEntry("Case 1", "Order", order2) // same folder + same label → same sanitized name
        };
        var zipPath = Path.Combine(_tempDir, "out.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries);

        Assert.Equal(2, written);
        using var archive = ZipFile.OpenRead(zipPath);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(2, names.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // never overwrote one another
    }

    [Fact]
    public void Build_returns_zero_and_still_produces_a_valid_empty_archive_when_every_file_is_missing()
    {
        var entries = new[] { new LitigationOrderArchiveEntry("Case 1", "gone", Path.Combine(_tempDir, "nope.pdf")) };
        var zipPath = Path.Combine(_tempDir, "out.zip");

        var written = LitigationOrdersArchiveBuilder.Build(zipPath, entries);

        Assert.Equal(0, written);
        using var archive = ZipFile.OpenRead(zipPath); // never throws — a valid (empty) zip, not a corrupt/missing file
        Assert.Empty(archive.Entries);
    }
}
