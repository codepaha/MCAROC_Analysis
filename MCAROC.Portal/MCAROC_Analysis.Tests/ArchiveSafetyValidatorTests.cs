using System.IO.Compression;
using MCAROC_Analysis.Services.McaFilings;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class ArchiveSafetyValidatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "archive-safety-tests-" + Guid.NewGuid().ToString("N"));

    public ArchiveSafetyValidatorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string CreateZip(string zipName, Action<ZipArchive> populate)
    {
        var path = Path.Combine(_tempDir, zipName);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
            populate(archive);
        return path;
    }

    [Fact]
    public void RejectsZipSlipEntry()
    {
        var path = CreateZip("slip.zip", archive =>
        {
            var entry = archive.CreateEntry("../../evil.pdf");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("payload");
        });

        var result = ArchiveSafetyValidator.ValidateOuterArchive(path, ArchiveSafetyLimits.Default);

        Assert.False(result.IsValid);
        Assert.Contains("unsafe path", result.Error);
    }

    [Fact]
    public void RejectsAbsolutePathEntry()
    {
        Assert.False(ArchiveSafetyValidator.IsPathSafe(@"C:\Windows\evil.pdf"));
        Assert.False(ArchiveSafetyValidator.IsPathSafe("/etc/passwd"));
    }

    [Fact]
    public void AcceptsNormalRelativeEntry()
    {
        Assert.True(ArchiveSafetyValidator.IsPathSafe("Charge Documents/70908_COASTAL_PROJECTS/file.pdf"));
    }

    [Fact]
    public void RejectsIndividualPdfExceedingSizeLimit()
    {
        // Real usage: PDFs only ever appear inside nested zips (see ValidateNestedArchive), never as
        // direct entries of the outer archive — the outer zip's entries are the nested zip files.
        var path = CreateZip("big.zip", archive =>
        {
            var entry = archive.CreateEntry("big.pdf", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(new byte[1000]);
        });

        var tinyLimits = ArchiveSafetyLimits.Default with { MaxIndividualPdfSizeBytes = 100 };
        var result = ArchiveSafetyValidator.ValidateNestedArchive(path, tinyLimits, currentDepth: 2, new CumulativeArchiveStats());

        Assert.False(result.IsValid);
        Assert.Contains("size limit", result.Error);
    }

    [Fact]
    public void RejectsArchiveExceedingPdfCountLimit()
    {
        var path = CreateZip("many.zip", archive =>
        {
            for (var i = 0; i < 5; i++)
            {
                var entry = archive.CreateEntry($"doc-{i}.pdf");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("x");
            }
        });

        var tinyLimits = ArchiveSafetyLimits.Default with { MaxPdfCount = 3 };
        var result = ArchiveSafetyValidator.ValidateNestedArchive(path, tinyLimits, currentDepth: 2, new CumulativeArchiveStats());

        Assert.False(result.IsValid);
        Assert.Contains("PDF limit", result.Error);
    }

    [Fact]
    public void CumulativeUncompressedSize_IsEnforcedAcrossMultipleNestedZipsNotPerZip()
    {
        // Regression test for a real gap: an outer zip can contain many individually-small-but-compressed
        // nested zips whose combined uncompressed content exhausts disk, even though none of them alone
        // exceeds the limit. The fix threads one CumulativeArchiveStats through every nested-zip
        // validation in a batch's unpack, so the limit applies to their sum, not each in isolation.
        var limits = ArchiveSafetyLimits.Default with { MaxUncompressedSizeBytes = 1500 };
        var stats = new CumulativeArchiveStats();

        var firstZip = CreateZip("first.zip", archive =>
        {
            var entry = archive.CreateEntry("a.pdf", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(new byte[1000]); // under the 1500-byte limit on its own
        });
        var firstResult = ArchiveSafetyValidator.ValidateNestedArchive(firstZip, limits, currentDepth: 2, stats);
        Assert.True(firstResult.IsValid);

        var secondZip = CreateZip("second.zip", archive =>
        {
            var entry = archive.CreateEntry("b.pdf", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(new byte[1000]); // also under 1500 alone, but 1000+1000 > 1500 cumulatively
        });
        var secondResult = ArchiveSafetyValidator.ValidateNestedArchive(secondZip, limits, currentDepth: 2, stats);

        Assert.False(secondResult.IsValid);
        Assert.Contains("cumulative uncompressed size", secondResult.Error);
    }

    [Fact]
    public void CumulativePdfCount_IsEnforcedAcrossMultipleNestedZips()
    {
        var limits = ArchiveSafetyLimits.Default with { MaxPdfCount = 3 };
        var stats = new CumulativeArchiveStats();

        string MakeZipWithPdfs(string name, int count)
        {
            return CreateZip(name, archive =>
            {
                for (var i = 0; i < count; i++)
                {
                    var entry = archive.CreateEntry($"doc-{i}.pdf");
                    using var writer = new StreamWriter(entry.Open());
                    writer.Write("x");
                }
            });
        }

        var firstResult = ArchiveSafetyValidator.ValidateNestedArchive(MakeZipWithPdfs("z1.zip", 2), limits, 2, stats);
        Assert.True(firstResult.IsValid);

        var secondResult = ArchiveSafetyValidator.ValidateNestedArchive(MakeZipWithPdfs("z2.zip", 2), limits, 2, stats);
        Assert.False(secondResult.IsValid); // 2 + 2 = 4 > limit of 3
    }

    [Fact]
    public void RejectsNestingBeyondMaxDepth()
    {
        var path = CreateZip("nested.zip", archive =>
        {
            var entry = archive.CreateEntry("inner.pdf");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("x");
        });

        var result = ArchiveSafetyValidator.ValidateNestedArchive(path, ArchiveSafetyLimits.Default, currentDepth: 3, new CumulativeArchiveStats());

        Assert.False(result.IsValid);
        Assert.Contains("nesting", result.Error);
    }

    [Fact]
    public void AcceptsAWellFormedArchiveWithinLimits()
    {
        var path = CreateZip("good.zip", archive =>
        {
            var entry = archive.CreateEntry("70908_COASTAL_PROJECTS_U45203OR1995PLC003982.zip");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("payload");
        });

        var result = ArchiveSafetyValidator.ValidateOuterArchive(path, ArchiveSafetyLimits.Default);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ResolveSafeExtractionPath_ThrowsWhenEntryEscapesTargetDirectory()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ArchiveSafetyValidator.ResolveSafeExtractionPath(_tempDir, "../outside.pdf"));
    }

    [Fact]
    public void ResolveSafeExtractionPath_ReturnsPathInsideTargetDirectoryForNormalEntry()
    {
        var resolved = ArchiveSafetyValidator.ResolveSafeExtractionPath(_tempDir, "doc.pdf");

        Assert.StartsWith(Path.GetFullPath(_tempDir), resolved);
    }
}
