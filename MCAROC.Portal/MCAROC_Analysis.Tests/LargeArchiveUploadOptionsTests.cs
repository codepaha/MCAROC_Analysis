using MCAROC_Analysis.Services.McaFilings;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LargeArchiveUploadOptionsTests
{
    [Fact]
    public void DefaultOptions_EnforceWorstCaseStorageReservationBudget()
    {
        var options = new LargeArchiveUploadOptions
        {
            MaxUncompressedSizeBytes = 20L * 1024 * 1024 * 1024, // 20 GiB
            MaxNestedTempBytes = 1L * 1024 * 1024 * 1024, // 1 GiB
            MinFreeDiskHeadroomBytes = 10L * 1024 * 1024 * 1024, // 10 GiB
            ChunkSizeBytes = 64 * 1024 * 1024 // 64 MiB
        };

        // 1 GiB archive
        var oneGiB = 1L * 1024 * 1024 * 1024;
        var destBudget1 = options.CalculateDestinationVolumeWorstCaseBytes(oneGiB);
        var stagingBudget1 = options.CalculateStagingVolumeBytes(oneGiB);

        // 1 + 20 + 1 + 10 = 32 GiB (or 33 GiB with buffer)
        Assert.Equal(32L * 1024 * 1024 * 1024, destBudget1);
        Assert.Equal(oneGiB + 64 * 1024 * 1024, stagingBudget1);

        // 5 GiB archive
        var fiveGiB = 5L * 1024 * 1024 * 1024;
        var destBudget5 = options.CalculateDestinationVolumeWorstCaseBytes(fiveGiB);
        Assert.Equal(36L * 1024 * 1024 * 1024, destBudget5);
    }

    [Fact]
    public void ArchiveSafetyLimits_FromOptions_TranslatesLimitsAccurately()
    {
        var options = new LargeArchiveUploadOptions
        {
            MaxArchiveSizeBytes = 5_368_709_120L,
            MaxUncompressedSizeBytes = 21_474_836_480L,
            MaxPdfCount = 10_000,
            MaxIndividualPdfSizeBytes = 250_000_000L
        };

        var limits = ArchiveSafetyLimits.FromOptions(options);

        Assert.Equal(options.MaxArchiveSizeBytes, limits.MaxArchiveSizeBytes);
        Assert.Equal(options.MaxUncompressedSizeBytes, limits.MaxUncompressedSizeBytes);
        Assert.Equal(2, limits.MaxNestedDepth);
        Assert.Equal(options.MaxPdfCount, limits.MaxPdfCount);
        Assert.Equal(options.MaxIndividualPdfSizeBytes, limits.MaxIndividualPdfSizeBytes);
    }

    [Fact]
    public void ValidateZipHeader_IdentifiesValidAndCorruptedHeaders()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Write corrupt header
            File.WriteAllBytes(tempFile, [0x00, 0x01, 0x02, 0x03]);
            var check1 = ArchiveSafetyValidator.ValidateZipHeader(tempFile);
            Assert.False(check1.IsValid);
            Assert.Contains("signature", check1.Error);

            // Write standard ZIP header (PK\x03\x04)
            File.WriteAllBytes(tempFile, [0x50, 0x4B, 0x03, 0x04]);
            var check2 = ArchiveSafetyValidator.ValidateZipHeader(tempFile);
            Assert.True(check2.IsValid);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
