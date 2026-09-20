using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Drives one <see cref="LitigationSearchJob"/> end to end: atomic claim, authenticate, register
/// (skipped if already registered — idempotent), poll report/job/{id} until a terminal payload arrives or
/// the poll budget is exhausted, then retain the raw bytes for #242 to parse. Mirrors
/// <c>CalculationAiAuditOrchestrator</c>'s claim/lease/backoff/recovery shape exactly — same reasoning: an
/// external vendor call that can genuinely still be in flight when the app restarts must not be
/// double-fired by recovery.</summary>
public sealed class LitigationSearchJobService(
    AppDbContext db,
    BprLitigationClient client,
    LitigationSearchQueue queue,
    IOptions<BprLitigationOptions> options,
    ILogger<LitigationSearchJobService> logger)
{
    private static readonly string LeaseOwnerId = $"{Environment.MachineName}:{Environment.ProcessId}";

    private readonly BprLitigationOptions _opts = options.Value;

    /// <summary>Margin added on top of the configured poll timeout to get the lease duration — deliberately
    /// derived rather than independently configured, so the two numbers can't drift apart (same reasoning as
    /// CalculationAiAuditOrchestrator.ComputeLeaseSeconds).</summary>
    private const int LeaseMarginSeconds = 120;

    private int LeaseSeconds => _opts.PollTimeoutMinutes * 60 + LeaseMarginSeconds;

    // ── Creation ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates (or, for a request that already has one, resets) the job row and returns it. The
    /// caller enqueues it — separated so a controller can save the request first. <paramref name="keywords"/>
    /// must already be the approved plan (see <see cref="LitigationKeywordPlanner"/>) — this service never
    /// invents search terms.</summary>
    public async Task<LitigationSearchJob> CreateOrResetJobAsync(
        long requestId, IReadOnlyList<LitigationKeyword> keywords, string entityType, string applicationCustomerId, CancellationToken ct)
    {
        if (keywords.Count == 0) throw new ArgumentException("At least one approved keyword is required.", nameof(keywords));

        var job = await db.LitigationSearchJobs.FirstOrDefaultAsync(j => j.RequestId == requestId, ct);
        if (job is null)
        {
            job = new LitigationSearchJob { RequestId = requestId, CreatedUtc = DateTime.UtcNow };
            db.LitigationSearchJobs.Add(job);
        }

        job.EntityType = entityType;
        job.ApplicationCustomerId = applicationCustomerId;
        job.KeywordsJson = JsonSerializer.Serialize(keywords.Select(k => new { value = k.Value, source = k.Source.ToString() }));
        job.Status = LitigationSearchJobStatus.Pending;
        job.ProgressPercent = 0;
        job.StatusMessage = "Queued.";
        job.FailureReason = null;
        job.VendorJobId = null;
        job.RegisteredUtc = null;
        job.NextAttemptUtc = null;
        job.CompletedUtc = null;
        await db.SaveChangesAsync(ct);
        return job;
    }

    // ── Processing ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Atomic Pending→Authenticating claim (bumping AttemptCount and taking a durable lease sized
    /// for the whole poll budget), then authenticate → register (unless already registered) → poll. The
    /// claim also requires NextAttemptUtc to have passed, so a backoff-scheduled retry that reaches the
    /// queue early is declined here instead of processed ahead of schedule.</summary>
    public async Task ProcessAsync(long jobId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var claimed = await db.LitigationSearchJobs
            .Where(j => j.LitigationSearchJobId == jobId && j.Status == LitigationSearchJobStatus.Pending
                && (j.NextAttemptUtc == null || j.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Authenticating)
                .SetProperty(j => j.AttemptCount, j => j.AttemptCount + 1)
                .SetProperty(j => j.LeaseOwner, LeaseOwnerId)
                .SetProperty(j => j.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds))
                .SetProperty(j => j.StartedUtc, now)
                .SetProperty(j => j.StatusMessage, "Authenticating with BPR."), ct);
        if (claimed == 0)
        {
            var current = await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == jobId)
                .Select(j => new { j.Status, j.NextAttemptUtc }).FirstOrDefaultAsync(ct);
            if (current is { Status: LitigationSearchJobStatus.Pending, NextAttemptUtc: { } next } && next > now)
                ScheduleRetry(jobId, next, ct);
            return;
        }

        var job = await db.LitigationSearchJobs.FirstAsync(j => j.LitigationSearchJobId == jobId, ct);

        try
        {
            var token = await client.AuthenticateAsync(ct);

            if (job.VendorJobId is null)
            {
                job.Status = LitigationSearchJobStatus.Registering;
                job.StatusMessage = "Registering search with BPR.";
                await db.SaveChangesAsync(ct);

                var keywords = ParseKeywordValues(job.KeywordsJson);
                var vendorJobId = await client.RegisterJobAsync(token, keywords, job.EntityType, job.ApplicationCustomerId, ct);

                job.VendorJobId = vendorJobId;
                job.RegisteredUtc = DateTime.UtcNow;
            }

            job.Status = LitigationSearchJobStatus.Polling;
            job.StatusMessage = "Waiting for BPR to complete the search.";
            job.ProgressPercent = 25;
            await db.SaveChangesAsync(ct);

            await PollUntilCompleteAsync(job, token, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "BPR litigation search failed for job {JobId} (attempt {Attempt})", jobId, job.AttemptCount);

            if (job.AttemptCount < _opts.MaxAttempts)
            {
                var nextAttemptUtc = DateTime.UtcNow + BackoffDelay(job.AttemptCount);
                await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == jobId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, LitigationSearchJobStatus.Pending)
                        .SetProperty(j => j.NextAttemptUtc, nextAttemptUtc)
                        .SetProperty(j => j.StatusMessage, "Retrying after a failed attempt.")
                        .SetProperty(j => j.FailureReason, ex.Message), ct);
                ScheduleRetry(jobId, nextAttemptUtc, ct);
            }
            else
            {
                await db.LitigationSearchJobs.Where(j => j.LitigationSearchJobId == jobId)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(j => j.Status, LitigationSearchJobStatus.Failed)
                        .SetProperty(j => j.StatusMessage, "Failed.")
                        .SetProperty(j => j.FailureReason, ex.Message)
                        .SetProperty(j => j.CompletedUtc, DateTime.UtcNow), ct);
                logger.LogError(ex, "BPR litigation search job {JobId} failed permanently after {Attempts} attempts", jobId, job.AttemptCount);
            }
        }
    }

    private async Task PollUntilCompleteAsync(LitigationSearchJob job, string token, CancellationToken ct)
    {
        var deadlineUtc = (job.RegisteredUtc ?? DateTime.UtcNow).AddMinutes(_opts.PollTimeoutMinutes);

        while (true)
        {
            var result = await client.GetReportAsync(token, job.VendorJobId!, ct);

            switch (result.Status)
            {
                case BprReportPollStatus.Completed:
                    var bytes = result.Bytes!;
                    job.Status = LitigationSearchJobStatus.Completed;
                    job.ReportFormat = result.Format;
                    job.RawReportBytes = bytes;
                    job.RawReportByteLength = bytes.LongLength;
                    job.RawResponseHash = ComputeHash(bytes);
                    job.ProgressPercent = 100;
                    job.StatusMessage = $"Report received ({result.Format}).";
                    job.FailureReason = null;
                    job.CompletedUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "BPR litigation search job {JobId} completed: {Format}, {ByteCount} bytes.",
                        job.LitigationSearchJobId, result.Format, bytes.LongLength);
                    return;

                case BprReportPollStatus.Failed:
                    throw new BprLitigationException(result.Message ?? "BPR report retrieval failed with no reason given.");

                case BprReportPollStatus.Pending:
                default:
                    if (DateTime.UtcNow >= deadlineUtc)
                        throw new BprLitigationException(
                            $"BPR report for vendor job {job.VendorJobId} did not complete within {_opts.PollTimeoutMinutes} minutes.");

                    job.StatusMessage = result.Message ?? "Waiting for BPR to complete the search.";
                    await db.SaveChangesAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(_opts.PollIntervalSeconds), ct);
                    continue;
            }
        }
    }

    /// <summary>Exponential backoff, capped at 5 minutes — identical formula to
    /// <c>CalculationAiAuditOrchestrator.BackoffDelay</c>, kept local rather than shared because the two
    /// types have no other coupling and a shared helper would be a premature abstraction over one line of
    /// math.</summary>
    internal static TimeSpan BackoffDelay(int attemptCount) =>
        TimeSpan.FromSeconds(Math.Min(300, 5 * Math.Pow(3, Math.Max(0, attemptCount - 1))));

    private void ScheduleRetry(long jobId, DateTime nextAttemptUtc, CancellationToken ct)
    {
        var delay = nextAttemptUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            queue.Enqueue(jobId);
            return;
        }

        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ct);
                capturedQueue.Enqueue(jobId);
            }
            catch (OperationCanceledException)
            {
                // App shutting down — the row stays Pending with NextAttemptUtc set; the next startup's
                // recovery sweep re-schedules or re-enqueues it.
            }
        }, ct);
    }

    /// <summary>Re-enqueues eligible Pending/in-flight rows on startup. An Authenticating/Registering/Polling
    /// row is only reset if its lease has actually expired (or predates this column) — a still-genuinely-
    /// running search is left alone rather than requeued into a duplicate BPR registration.</summary>
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inFlightStatuses = new[]
        {
            LitigationSearchJobStatus.Authenticating, LitigationSearchJobStatus.Registering, LitigationSearchJobStatus.Polling
        };

        await db.LitigationSearchJobs
            .Where(j => inFlightStatuses.Contains(j.Status) && (j.LeaseExpiresUtc == null || j.LeaseExpiresUtc < now))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, LitigationSearchJobStatus.Pending)
                .SetProperty(j => j.StatusMessage, "Resuming after an application restart."), ct);

        var pending = await db.LitigationSearchJobs
            .Where(j => j.Status == LitigationSearchJobStatus.Pending)
            .Select(j => new { j.LitigationSearchJobId, j.NextAttemptUtc })
            .ToListAsync(ct);

        foreach (var p in pending)
        {
            if (p.NextAttemptUtc is { } next && next > now)
                ScheduleRetry(p.LitigationSearchJobId, next, ct);
            else
                queue.Enqueue(p.LitigationSearchJobId);
        }

        return pending.Count;
    }

    private static IReadOnlyList<string> ParseKeywordValues(string keywordsJson)
    {
        using var document = JsonDocument.Parse(keywordsJson);
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("value").GetString() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToList();
    }

    private static string ComputeHash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
