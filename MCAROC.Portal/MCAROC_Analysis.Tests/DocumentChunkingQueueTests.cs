using MCAROC_Analysis.Services.Chat;

namespace MCAROC_Analysis.Tests;

/// <summary>The queue coalesces a batch across its whole lifecycle — Queued, then Running once the
/// worker calls MarkStarted, then gone (or re-Queued exactly once) once MarkDone runs — not just while it
/// waits in the channel. This is the PR #225 review finding: with FilingBatchProcessor now enqueueing the
/// batch on every document completion, a marker cleared on dequeue let each completion during an
/// in-flight pass start another overlapping full-batch scan. Each test drains the way
/// DocumentChunkingWorker does: dequeue, MarkStarted, "process", MarkDone.</summary>
public class DocumentChunkingQueueTests
{
    /// <summary>Reads until the channel stays quiet for <paramref name="quietMs"/>, replaying the real
    /// worker's MarkStarted → process → MarkDone sequence for each item. <paramref name="onEach"/> runs
    /// between MarkStarted and MarkDone, like work inside a real pass.</summary>
    private static async Task<List<long>> DrainAsync(DocumentChunkingQueue queue, int quietMs = 200, Action<long>? onEach = null)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(quietMs));
        var received = new List<long>();
        try
        {
            await foreach (var id in queue.ReadAllAsync(cts.Token))
            {
                queue.MarkStarted(id);
                received.Add(id);
                onEach?.Invoke(id);
                queue.MarkDone(id);
                cts.CancelAfter(quietMs); // reset the quiet window after every item
            }
        }
        catch (OperationCanceledException) { }
        return received;
    }

    [Fact]
    public async Task Enqueue_coalesces_duplicate_batchIds_still_waiting()
    {
        var queue = new DocumentChunkingQueue();
        for (var i = 0; i < 5; i++)
            queue.Enqueue(42L);

        var received = await DrainAsync(queue);

        // All 5 happened before the single dequeue — none of them were "during a running pass", so this
        // must collapse to exactly one run, not a run plus a follow-up.
        Assert.Equal(new[] { 42L }, received);
        Assert.False(queue.IsActive(42L));
    }

    [Fact]
    public async Task Enqueues_during_a_running_pass_collapse_into_exactly_one_follow_up_pass()
    {
        var queue = new DocumentChunkingQueue();
        queue.Enqueue(7L);

        var passes = 0;
        var received = await DrainAsync(queue, quietMs: 300, onEach: _ =>
        {
            passes++;
            if (passes > 1) return;
            // 50 documents finish while the first pass is still running — the review's scenario.
            for (var i = 0; i < 50; i++)
                queue.Enqueue(7L);
        });

        // One pass plus exactly one follow-up, never 51 overlapping scans.
        Assert.Equal(new[] { 7L, 7L }, received);
        Assert.False(queue.IsActive(7L));
    }

    [Fact]
    public async Task A_batch_stays_active_from_dequeue_through_MarkStarted_until_MarkDone()
    {
        var queue = new DocumentChunkingQueue();
        queue.Enqueue(9L);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var id in queue.ReadAllAsync(cts.Token))
        {
            Assert.Equal(9L, id);
            Assert.True(queue.IsActive(9L)); // dequeued, before MarkStarted — still active
            queue.MarkStarted(id);
            queue.Enqueue(9L); // a completion mid-pass: must not be silently dropped
            queue.MarkDone(9L); // pass ends → the flagged follow-up is queued once
            break;
        }

        Assert.True(queue.IsActive(9L)); // the follow-up is now Queued
        var received = await DrainAsync(queue);
        Assert.Equal(new[] { 9L }, received);
        Assert.False(queue.IsActive(9L));
    }

    [Fact]
    public void Enqueue_while_queued_but_not_yet_started_is_still_a_plain_no_op()
    {
        var queue = new DocumentChunkingQueue();
        queue.Enqueue(5L);
        queue.Enqueue(5L); // still just Queued — no MarkStarted yet — must not flag a follow-up

        queue.MarkStarted(5L);
        queue.MarkDone(5L); // if the second Enqueue had wrongly flagged a follow-up, this would re-queue it

        Assert.False(queue.IsActive(5L));
    }

    [Fact]
    public void MarkDone_for_an_unknown_batch_is_a_no_op()
    {
        var queue = new DocumentChunkingQueue();
        queue.MarkDone(123L);
        Assert.False(queue.IsActive(123L));
    }

    [Fact]
    public async Task Different_batches_are_independent()
    {
        var queue = new DocumentChunkingQueue();
        queue.Enqueue(1L);
        queue.Enqueue(2L);
        queue.Enqueue(1L);

        var received = await DrainAsync(queue);

        Assert.Equal(new[] { 1L, 2L }, received);
    }
}
