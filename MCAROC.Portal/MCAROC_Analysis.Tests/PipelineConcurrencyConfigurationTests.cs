using System.Reflection;
using MCAROC_Analysis.Services.Chat;
using MCAROC_Analysis.Services.McaFilings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves LargeArchiveUploadOptions' concurrency settings actually size the workers' semaphores
/// instead of being config that's declared but never read (the state MaxConcurrentUploads/MaxConcurrentUnpacks
/// were in before this change — see LargeArchiveUploadOptions' own remarks). A SemaphoreSlim doesn't expose
/// its initial capacity directly, but CurrentCount immediately after construction (before anything has
/// acquired it) equals that capacity, so reading it via reflection is a faithful, non-invasive check.</summary>
public class PipelineConcurrencyConfigurationTests
{
    private static int SemaphoreCount(object instance, string fieldName)
    {
        var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var semaphore = Assert.IsType<SemaphoreSlim>(field!.GetValue(instance));
        return semaphore.CurrentCount;
    }

    [Fact]
    public void FilingProcessingWorker_sizes_its_three_semaphores_from_configured_options()
    {
        var options = Options.Create(new LargeArchiveUploadOptions
        {
            MaxConcurrentUnpacks = 3,
            MaxConcurrentDocumentProcessing = 7,
            MaxConcurrentAiExtraction = 5
        });
        var worker = new FilingProcessingWorker(scopeFactory: null!, queue: new FilingProcessingQueue(), NullLogger<FilingProcessingWorker>.Instance, options);

        Assert.Equal(3, SemaphoreCount(worker, "_unpackConcurrency"));
        Assert.Equal(7, SemaphoreCount(worker, "_documentConcurrency"));
        Assert.Equal(5, SemaphoreCount(worker, "_extractionConcurrency"));
    }

    [Fact]
    public void FilingProcessingWorker_defaults_match_the_original_hardcoded_values_when_unconfigured()
    {
        var worker = new FilingProcessingWorker(scopeFactory: null!, queue: new FilingProcessingQueue(), NullLogger<FilingProcessingWorker>.Instance, options: null);

        Assert.Equal(1, SemaphoreCount(worker, "_unpackConcurrency"));
        Assert.Equal(4, SemaphoreCount(worker, "_documentConcurrency"));
        Assert.Equal(2, SemaphoreCount(worker, "_extractionConcurrency"));
    }

    [Fact]
    public void DocumentChunkingWorker_sizes_its_semaphore_from_configured_options()
    {
        var options = Options.Create(new LargeArchiveUploadOptions { MaxConcurrentChunking = 9 });
        var worker = new DocumentChunkingWorker(scopeFactory: null!, queue: new DocumentChunkingQueue(), NullLogger<DocumentChunkingWorker>.Instance, options);

        Assert.Equal(9, SemaphoreCount(worker, "_concurrency"));
    }

    [Fact]
    public void DocumentChunkingWorker_defaults_to_four_when_unconfigured()
    {
        var worker = new DocumentChunkingWorker(scopeFactory: null!, queue: new DocumentChunkingQueue(), NullLogger<DocumentChunkingWorker>.Instance, options: null);

        Assert.Equal(4, SemaphoreCount(worker, "_concurrency"));
    }
}
