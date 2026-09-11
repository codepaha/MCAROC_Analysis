using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.PreLoginReports;

public sealed class PreLoginReportJobService(AppDbContext db, PreLoginReportQueue queue, PreLoginReportService reports, IWebHostEnvironment environment)
{
    public async Task<Guid> QueueBatchAsync(IEnumerable<string> cins, PreLoginReportFormat format, CancellationToken cancellationToken)
    {
        var normalized = ValidateCins(cins.Take(25));
        var batch = Guid.NewGuid();
        foreach (var cin in normalized)
        {
            db.PreLoginReportJobs.Add(new PreLoginReportJob { BatchId = batch, Cin = cin, Format = format.ToString(), Status = PreLoginReportJobStatus.Queued, ProgressPercent = 0, CreatedUtc = DateTime.UtcNow });
        }
        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in await db.PreLoginReportJobs.Where(x => x.BatchId == batch).Select(x => x.PreLoginReportJobId).ToListAsync(cancellationToken)) queue.Enqueue(id);
        return batch;
    }

    /// <summary>Queues a single CIN the same way as a 1-CIN batch — the single-CIN and batch entry points
    /// share one pipeline, so a single-CIN request also lands in History and never blocks the browser on a
    /// slow live MCA fetch.</summary>
    public async Task<Guid> QueueSingleAsync(string cin, string? submittedCompanyName, PreLoginReportFormat format, CancellationToken cancellationToken)
    {
        var normalized = ValidateCins([cin]);
        var batch = Guid.NewGuid();
        db.PreLoginReportJobs.Add(new PreLoginReportJob
        {
            BatchId = batch, Cin = normalized[0], SubmittedCompanyName = submittedCompanyName, Format = format.ToString(),
            Status = PreLoginReportJobStatus.Queued, ProgressPercent = 0, CreatedUtc = DateTime.UtcNow
        });
        await db.SaveChangesAsync(cancellationToken);
        var id = await db.PreLoginReportJobs.Where(x => x.BatchId == batch).Select(x => x.PreLoginReportJobId).SingleAsync(cancellationToken);
        queue.Enqueue(id);
        return batch;
    }

    private static IReadOnlyList<string> ValidateCins(IEnumerable<string> cins)
    {
        var normalized = cins.Select(x => x.Trim().ToUpperInvariant()).Where(x => x.Length > 0).Distinct().ToList();
        if (normalized.Count == 0) throw new PreLoginReportException("Enter at least one CIN.");
        if (normalized.Any(cin => !Regex.IsMatch(cin, "^[LU][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}$")))
            throw new PreLoginReportException("Only company CINs are supported. LLPINs are not supported by the configured API.");
        return normalized;
    }

    public async Task<IReadOnlyList<PreLoginReportJob>> HistoryAsync(CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.OrderByDescending(x => x.CreatedUtc).Take(200).ToListAsync(cancellationToken);

    public async Task<PreLoginReportJob?> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.FindAsync([id], cancellationToken);

    /// <summary>Loads a completed job's fetched data (captured in DataJson right after fetch, before
    /// generation) as an editable draft — the basis for the optional "Edit" action on the History page.</summary>
    public async Task<PreLoginReportDraftViewModel> GetEditableDraftAsync(long id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (string.IsNullOrWhiteSpace(job.DataJson))
            throw new PreLoginReportException("This report has no fetched data available to edit.");
        var data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson)
            ?? throw new PreLoginReportException("Stored report data is corrupt.");
        return PreLoginReportService.ToDraft(job.PreLoginReportJobId, job.Cin, Enum.Parse<PreLoginReportFormat>(job.Format), data);
    }

    /// <summary>Applies a user's edits (including any added/removed charge or director rows) and
    /// regenerates the stored report in place.</summary>
    public async Task ApplyEditAndRegenerateAsync(long id, PreLoginReportDraftViewModel draft, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        var format = Enum.Parse<PreLoginReportFormat>(job.Format);
        var data = PreLoginReportService.ApplyEdits(draft);
        var generated = await reports.GenerateFromDataAsync(job.Cin, format, data, cancellationToken);
        if (!string.IsNullOrWhiteSpace(job.ReportStoragePath) && File.Exists(job.ReportStoragePath)) File.Delete(job.ReportStoragePath);
        job.ReportStoragePath = await StoreReportAsync(job.PreLoginReportJobId, generated, cancellationToken);
        job.DataJson = JsonSerializer.Serialize(data);
        job.Status = PreLoginReportJobStatus.Completed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> StoreReportAsync(long jobId, GeneratedReport generated, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(environment.ContentRootPath, "App_Data", "PreLoginReports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{jobId}-{generated.FileName}");
        await File.WriteAllBytesAsync(path, generated.Bytes, cancellationToken);
        return path;
    }

    public async Task RerunAsync(long id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        job.Status = PreLoginReportJobStatus.Queued; job.ProgressPercent = 0; job.AttemptCount = 0; job.FailureReason = null; job.NextAttemptUtc = null; job.ReportStoragePath = null; job.CompletedUtc = null;
        await db.SaveChangesAsync(cancellationToken); queue.Enqueue(job.PreLoginReportJobId);
    }

    public async Task ProcessAsync(long id, CancellationToken cancellationToken)
    {
        var job = await FindAsync(id, cancellationToken);
        if (job is null || job.Status is PreLoginReportJobStatus.Completed or PreLoginReportJobStatus.Generating) return;
        if (job.NextAttemptUtc is { } next && next > DateTime.UtcNow) { Schedule(id, next - DateTime.UtcNow); return; }
        job.Status = PreLoginReportJobStatus.Fetching; job.ProgressPercent = 10; job.StartedUtc ??= DateTime.UtcNow; job.AttemptCount++;
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            var format = Enum.Parse<PreLoginReportFormat>(job.Format);
            var data = await reports.FetchDataAsync(job.Cin, job.SubmittedCompanyName, cancellationToken);
            job.DataJson = JsonSerializer.Serialize(data); job.Status = PreLoginReportJobStatus.Generating; job.ProgressPercent = 60;
            await db.SaveChangesAsync(cancellationToken);
            var generated = await reports.GenerateFromDataAsync(job.Cin, format, data, cancellationToken);
            job.ReportStoragePath = await StoreReportAsync(job.PreLoginReportJobId, generated, cancellationToken);
            job.Status = PreLoginReportJobStatus.Completed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow; job.FailureReason = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (PreLoginReportException ex) { await FailOrRetryAsync(job, ex.Message, ex.Retryable, cancellationToken); }
        catch (Exception) { await FailOrRetryAsync(job, "Unexpected report processing failure.", retryable: true, cancellationToken); }
    }

    private async Task FailOrRetryAsync(PreLoginReportJob job, string reason, bool retryable, CancellationToken cancellationToken)
    {
        job.FailureReason = reason;
        if (retryable && job.AttemptCount < 3)
        {
            job.Status = PreLoginReportJobStatus.Queued; job.ProgressPercent = 0; job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(20 * job.AttemptCount);
            await db.SaveChangesAsync(cancellationToken);
            Schedule(job.PreLoginReportJobId, TimeSpan.FromSeconds(20 * job.AttemptCount));
            return;
        }
        job.Status = PreLoginReportJobStatus.Failed; job.ProgressPercent = 100; job.CompletedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void Schedule(long jobId, TimeSpan delay) => _ = Task.Run(async () => { await Task.Delay(delay); queue.Enqueue(jobId); });
}
