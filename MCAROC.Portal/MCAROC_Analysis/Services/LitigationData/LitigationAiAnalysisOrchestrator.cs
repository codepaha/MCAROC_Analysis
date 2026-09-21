using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Durable, request-scoped LIT-05 analysis. The channel only wakes a worker; the database is the
/// authority for admission, retries and leases, so duplicate HTTP calls and process restarts do not issue
/// concurrent paid model requests.</summary>
public sealed class LitigationAiAnalysisOrchestrator(
    AppDbContext db, ILitigationAiAnalysisClient client, LitigationAiAnalysisQueue queue,
    IOptions<LitigationAiAnalysisOptions> options, ILogger<LitigationAiAnalysisOrchestrator> logger)
{
    private static readonly string LeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int LeaseMarginSeconds = 60;
    private LitigationAiAnalysisOptions Options => options.Value;

    public async Task<LitigationAiAnalysisRun> CreateOrJoinAsync(long requestId, CancellationToken ct)
    {
        var active = await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == requestId &&
            (x.Status == LitigationAiAnalysisRunStatus.Pending || x.Status == LitigationAiAnalysisRunStatus.InProgress))
            .OrderByDescending(x => x.LitigationAiAnalysisRunId).FirstOrDefaultAsync(ct);
        if (active is not null) return active;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var next = (await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == requestId)
                .Select(x => (int?)x.RunNumber).MaxAsync(ct) ?? 0) + 1;
            var run = new LitigationAiAnalysisRun { RequestId = requestId, RunNumber = next,
                ModelId = VertexLitigationAiAnalysisClient.ModelId, PromptVersion = LitigationAnalysisPromptBuilder.PromptVersion,
                CreatedUtc = DateTime.UtcNow };
            db.LitigationAiAnalysisRuns.Add(run);
            try
            {
                await db.SaveChangesAsync(ct);
                queue.Enqueue(run.LitigationAiAnalysisRunId);
                return run;
            }
            catch (DbUpdateException)
            {
                db.Entry(run).State = EntityState.Detached;
                active = await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == requestId &&
                    (x.Status == LitigationAiAnalysisRunStatus.Pending || x.Status == LitigationAiAnalysisRunStatus.InProgress))
                    .OrderByDescending(x => x.LitigationAiAnalysisRunId).FirstOrDefaultAsync(ct);
                if (active is not null) return active;
            }
        }
        throw new InvalidOperationException("Unable to admit litigation analysis run after concurrent updates.");
    }

    public async Task RunAsync(long runId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var token = Guid.NewGuid();
        var claimed = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == runId &&
            x.Status == LitigationAiAnalysisRunStatus.Pending && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisRunStatus.InProgress)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1).SetProperty(x => x.LeaseOwner, LeaseOwner)
                .SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseExpiresUtc, now.AddSeconds(Options.TimeoutSeconds + LeaseMarginSeconds))
                .SetProperty(x => x.StartedUtc, now), ct);
        if (claimed == 0) return;

        var run = await db.LitigationAiAnalysisRuns.FirstAsync(x => x.LitigationAiAnalysisRunId == runId, ct);
        try
        {
            var cases = await db.LitigationCases.Where(x => x.RequestId == run.RequestId).ToListAsync(ct);
            var chunks = await db.LitigationOrderChunks.Where(x => x.RequestId == run.RequestId).ToListAsync(ct);
            foreach (var @case in cases)
            {
                var evidence = LitigationAnalysisPromptBuilder.BuildCaseEvidence(@case, chunks.Where(x => x.LitigationCaseId == @case.LitigationCaseId));
                var evidenceJson = LitigationAnalysisPromptBuilder.SerializeEvidence(evidence);
                var prompt = LitigationAnalysisPromptBuilder.BuildCasePrompt(evidence);
                var item = new LitigationCaseAiAnalysis { LitigationAiAnalysisRunId = runId, LitigationCaseId = @case.LitigationCaseId,
                    EvidenceJson = evidenceJson, EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(evidenceJson),
                    PromptHash = LitigationAnalysisPromptBuilder.ComputeHash(prompt) };
                db.LitigationCaseAiAnalyses.Add(item);
                if (evidence.Excerpts.Count == 0)
                {
                    item.Status = LitigationAiAnalysisItemStatus.InsufficientEvidence;
                    item.AnalysisJson = "{\"status\":\"InsufficientEvidence\",\"summary\":\"No retained order text is available.\",\"unknowns\":[\"Order text unavailable\"],\"evidenceReferences\":[]}";
                    item.CompletedUtc = DateTime.UtcNow;
                    continue;
                }
                var response = await client.CallAsync(prompt, Options.TimeoutSeconds, ct);
                item.RawResponseJson = response.RawResponse;
                item.ResponseHash = string.IsNullOrWhiteSpace(response.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(response.RawResponse);
                if (!response.Success) { item.Status = LitigationAiAnalysisItemStatus.Failed; item.FailureReason = response.FailureReason; item.CompletedUtc = DateTime.UtcNow; continue; }
                var valid = LitigationAnalysisResponseValidator.ValidateCase(response.RawResponse, evidence);
                item.Status = valid.IsAccepted ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed;
                item.AnalysisJson = valid.AnalysisJson; item.FailureReason = valid.RejectReason; item.CompletedUtc = DateTime.UtcNow;
            }
            var evidenceForPortfolio = JsonSerializer.Serialize(db.LitigationCaseAiAnalyses.Local.Select(x => new { x.LitigationCaseId, x.Status, x.AnalysisJson, x.EvidenceHash }));
            db.LitigationPortfolioAiAnalyses.Add(new LitigationPortfolioAiAnalysis { LitigationAiAnalysisRunId = runId,
                Status = LitigationAiAnalysisItemStatus.InsufficientEvidence, EvidenceJson = evidenceForPortfolio,
                EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(evidenceForPortfolio), PromptHash = LitigationAnalysisPromptBuilder.ComputeHash(evidenceForPortfolio),
                AnalysisJson = "{\"status\":\"InsufficientEvidence\",\"summary\":\"Portfolio synthesis is not generated without separately validated cross-case evidence.\",\"unknowns\":[\"Cross-case synthesis pending\"],\"evidenceReferences\":[]}", CompletedUtc = DateTime.UtcNow });
            run.Status = db.LitigationCaseAiAnalyses.Local.Any(x => x.Status == LitigationAiAnalysisItemStatus.Failed)
                ? LitigationAiAnalysisRunStatus.CompletedWithErrors : LitigationAiAnalysisRunStatus.Completed;
            run.CompletedUtc = DateTime.UtcNow; run.LeaseToken = null; run.LeaseExpiresUtc = null;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Litigation AI analysis run {RunId} failed", runId);
            var retry = run.AttemptCount < Options.MaxAttempts;
            var next = DateTime.UtcNow.AddSeconds(5 * Math.Pow(3, Math.Max(0, run.AttemptCount - 1)));
            await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == runId && x.LeaseToken == token)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, retry ? LitigationAiAnalysisRunStatus.Pending : LitigationAiAnalysisRunStatus.Failed)
                    .SetProperty(x => x.FailureReason, ex.Message).SetProperty(x => x.NextAttemptUtc, retry ? next : (DateTime?)null)
                    .SetProperty(x => x.CompletedUtc, retry ? (DateTime?)null : DateTime.UtcNow).SetProperty(x => x.LeaseToken, (Guid?)null), ct);
            if (retry) ScheduleRetry(runId, next, ct);
        }
    }

    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.InProgress &&
            (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisRunStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct);
        var pending = await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.Pending)
            .Select(x => new { x.LitigationAiAnalysisRunId, x.NextAttemptUtc }).ToListAsync(ct);
        foreach (var item in pending)
            if (item.NextAttemptUtc is { } next && next > now) ScheduleRetry(item.LitigationAiAnalysisRunId, next, ct);
            else queue.Enqueue(item.LitigationAiAnalysisRunId);
        return pending.Count;
    }

    private void ScheduleRetry(long runId, DateTime dueUtc, CancellationToken ct)
    {
        var delay = dueUtc - DateTime.UtcNow;
        if (delay <= TimeSpan.Zero) { queue.Enqueue(runId); return; }
        var capturedQueue = queue;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, ct); capturedQueue.Enqueue(runId); }
            catch (OperationCanceledException) { /* recovery re-schedules durable pending work on next start */ }
        }, ct);
    }
}
