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

    /// <summary>Jobs belonging to one batch — the only "history" a caller can see. There is no login on
    /// this pipeline, so the <see cref="PreLoginReportJob.BatchId"/> Guid IS the access credential: it is
    /// unguessable (random, 122 bits) and never listed anywhere, unlike the sequential
    /// <see cref="PreLoginReportJob.PreLoginReportJobId"/>. See #47 — the previous unscoped "return every
    /// job in the system" version let any anonymous visitor browse every user's CINs and download/rerun
    /// their reports by guessing/incrementing the bare id.</summary>
    public async Task<IReadOnlyList<PreLoginReportJob>> HistoryAsync(Guid batch, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.Where(x => x.BatchId == batch).OrderByDescending(x => x.CreatedUtc).ToListAsync(cancellationToken);

    /// <summary>Looks up a job by id alone — for the background worker only (<see cref="PreLoginReportWorker"/>
    /// via <see cref="ProcessAsync"/>), which has no batch context and is not attacker-controlled. Every
    /// externally-reachable lookup (download/edit/rerun) MUST go through <see cref="FindInBatchAsync"/>
    /// instead, or it reintroduces #47's ownership gap.</summary>
    public async Task<PreLoginReportJob?> FindAsync(long id, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.FindAsync([id], cancellationToken);

    /// <summary>The only job lookup safe to expose to an anonymous caller: returns null — identically,
    /// whether <paramref name="id"/> doesn't exist at all or exists under a DIFFERENT batch — so a caller
    /// who knows one batch's Guid can never probe for the existence of another batch's job ids (#47).</summary>
    public async Task<PreLoginReportJob?> FindInBatchAsync(Guid batch, long id, CancellationToken cancellationToken) =>
        await db.PreLoginReportJobs.FirstOrDefaultAsync(x => x.PreLoginReportJobId == id && x.BatchId == batch, cancellationToken);

    /// <summary>Loads a completed job's fetched data (captured in DataJson right after fetch, before
    /// generation) as an editable draft — the basis for the optional "Edit" action on the History page.
    /// Restricted to Completed jobs: DataJson is already populated once fetch finishes (status Generating),
    /// so without this check an edit opened against an in-flight job could regenerate concurrently with the
    /// worker's own ProcessAsync run and race it for job.ReportStoragePath/DataJson/Status. Restricted to
    /// <paramref name="batch"/> per #47 — see <see cref="FindInBatchAsync"/>.</summary>
    public async Task<PreLoginReportDraftViewModel> GetEditableDraftAsync(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (job.Status != PreLoginReportJobStatus.Completed)
            throw new PreLoginReportException("This report is still being generated. Wait for it to complete before editing.");
        if (string.IsNullOrWhiteSpace(job.DataJson))
            throw new PreLoginReportException("This report has no fetched data available to edit.");
        var data = JsonSerializer.Deserialize<InstaReportData>(job.DataJson)
            ?? throw new PreLoginReportException("Stored report data is corrupt.");
        return PreLoginReportService.ToDraft(job.PreLoginReportJobId, job.BatchId, job.Cin, Enum.Parse<PreLoginReportFormat>(job.Format), data);
    }

    /// <summary>Applies a user's edits (including any added/removed charge or director rows) and
    /// regenerates the stored report in place. Same Completed-only restriction as <see cref="GetEditableDraftAsync"/>
    /// — see that method's remarks for the race this closes. Restricted to <paramref name="batch"/> per #47.</summary>
    public async Task ApplyEditAndRegenerateAsync(Guid batch, long id, PreLoginReportDraftViewModel draft, CancellationToken cancellationToken)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
        if (job.Status != PreLoginReportJobStatus.Completed)
            throw new PreLoginReportException("This report is still being generated. Wait for it to complete before editing.");
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

    /// <summary>Restricted to <paramref name="batch"/> per #47 — see <see cref="FindInBatchAsync"/>.</summary>
    public async Task RerunAsync(Guid batch, long id, CancellationToken cancellationToken)
    {
        var job = await FindInBatchAsync(batch, id, cancellationToken) ?? throw new PreLoginReportException("Report request not found.");
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
