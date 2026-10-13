using MCAROC_Analysis.Services.McaFilings;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>Several requests' filings are processed side by side: work is handed out round-robin across batches, so a request never waits
/// behind another request's hundreds of documents (owner, 2026-10-13: 10-20 requests a day, 100 later; found when one 527-document batch sat
/// at 10 done behind another's 499).</summary>
public class FilingFairQueueTests
{
    private static async Task<List<long>> DrainAsync(FilingProcessingQueue q, FilingWorkKind kind, int n)
    {
        var ids = new List<long>();
        for (var i = 0; i < n; i++)
            ids.Add((await q.DequeueAsync(kind, default)) switch
            {
                ProcessDocumentWorkItem d => d.FilingDocumentId,
                ExtractFilingWorkItem e => e.FilingId,
                UnpackBatchWorkItem u => u.BatchId,
                _ => -1
            });
        return ids;
    }

    [Fact]
    public async Task Documents_of_two_batches_are_served_in_turn_not_one_batch_after_the_other()
    {
        var q = new FilingProcessingQueue();
        foreach (var id in new long[] { 101, 102, 103, 104 }) q.Enqueue(new ProcessDocumentWorkItem(id) { Lane = 1 });   // the first batch unpacked first
        foreach (var id in new long[] { 201, 202 }) q.Enqueue(new ProcessDocumentWorkItem(id) { Lane = 2 });

        Assert.Equal([101, 201, 102, 202, 103, 104], await DrainAsync(q, FilingWorkKind.Document, 6));
        Assert.Equal(0, q.Count(FilingWorkKind.Document));
    }

    [Fact]
    public async Task A_batch_that_arrives_later_is_served_on_the_next_turn()
    {
        var q = new FilingProcessingQueue();
        foreach (var id in Enumerable.Range(1, 500).Select(i => (long)i)) q.Enqueue(new ProcessDocumentWorkItem(id) { Lane = 1 });
        Assert.Equal([1, 2], await DrainAsync(q, FilingWorkKind.Document, 2));

        q.Enqueue(new ProcessDocumentWorkItem(9001) { Lane = 2 });   // a new request's first document, while 498 of the first wait

        var next = await DrainAsync(q, FilingWorkKind.Document, 2);
        Assert.Contains(9001L, next);                                 // served within one turn, not after 498 others
    }

    [Fact]
    public async Task Each_kind_of_work_has_its_own_queue_and_an_unpack_is_its_own_batch_lane()
    {
        var q = new FilingProcessingQueue();
        q.Enqueue(new ProcessDocumentWorkItem(5) { Lane = 7 });
        q.Enqueue(new ExtractFilingWorkItem(6) { Lane = 7 });
        q.Enqueue(new UnpackBatchWorkItem(7));

        Assert.Equal(1, q.Count(FilingWorkKind.Document));
        Assert.Equal(1, q.Count(FilingWorkKind.Extraction));
        var unpack = Assert.IsType<UnpackBatchWorkItem>(await q.DequeueAsync(FilingWorkKind.Unpack, default));
        Assert.Equal(7, unpack.Lane);
    }

    [Fact]
    public async Task A_worker_waits_for_work_and_takes_it_as_soon_as_it_is_queued()
    {
        var q = new FilingProcessingQueue();
        var waiting = q.DequeueAsync(FilingWorkKind.Extraction, default);
        Assert.False(waiting.IsCompleted);
        q.Enqueue(new ExtractFilingWorkItem(42) { Lane = 3 });
        Assert.Equal(42, ((ExtractFilingWorkItem)await waiting.WaitAsync(TimeSpan.FromSeconds(5))).FilingId);
    }

    [Fact]
    public void Document_processing_sizes_itself_to_the_machine_unless_configured()
    {
        Assert.Equal(Math.Max(2, Environment.ProcessorCount - 2), LargeArchiveUploadOptions.EffectiveDocumentConcurrency(0));
        Assert.Equal(6, LargeArchiveUploadOptions.EffectiveDocumentConcurrency(6));
        var counts = FilingProcessingWorker.WorkerCounts(new LargeArchiveUploadOptions { MaxConcurrentUnpacks = 2, MaxConcurrentDocumentProcessing = 0, MaxConcurrentAiExtraction = 4 });
        Assert.Equal((2, Math.Max(2, Environment.ProcessorCount - 2), 4), counts);
    }
}
