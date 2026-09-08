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
        var path = CreateZip("big.zip", archive =>
        {
            var entry = archive.CreateEntry("big.pdf", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            stream.Write(new byte[1000]);
        });

        var tinyLimits = ArchiveSafetyLimits.Default with { MaxIndividualPdfSizeBytes = 100 };
        var result = ArchiveSafetyValidator.ValidateOuterArchive(path, tinyLimits);

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
        var result = ArchiveSafetyValidator.ValidateOuterArchive(path, tinyLimits);

        Assert.False(result.IsValid);
        Assert.Contains("PDF limit", result.Error);
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

        var result = ArchiveSafetyValidator.ValidateNestedArchive(path, ArchiveSafetyLimits.Default, currentDepth: 3);

        Assert.False(result.IsValid);
        Assert.Contains("nesting", result.Error);
    }

    [Fact]
    public void AcceptsAWellFormedArchiveWithinLimits()
    {
        var path = CreateZip("good.zip", archive =>
        {
            var entry = archive.CreateEntry("70908_COASTAL_PROJECTS_U45203OR1995PLC003982/Certificates/doc.pdf");
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
