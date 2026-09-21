using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Database lease token fences every publication; a stale worker can neither persist results nor
/// complete a run after takeover. Persisted case/portfolio rows make retries idempotent.</summary>
public sealed class LitigationAiAnalysisOrchestrator(AppDbContext db, ILitigationAiAnalysisClient client,
    LitigationAiAnalysisQueue queue, IOptions<LitigationAiAnalysisOptions> options, ILogger<LitigationAiAnalysisOrchestrator> logger)
{
    private static readonly string LeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int LeaseMarginSeconds = 60;
    private int LeaseSeconds => options.Value.TimeoutSeconds + LeaseMarginSeconds;

    public async Task<LitigationAiAnalysisRun> CreateOrJoinAsync(long requestId, CancellationToken ct)
    {
        var active = await Active(requestId, ct); if (active is not null) return active;
        for (var i = 0; i < 3; i++)
        {
            var run = new LitigationAiAnalysisRun { RequestId = requestId, CreatedUtc = DateTime.UtcNow,
                RunNumber = (await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == requestId).Select(x => (int?)x.RunNumber).MaxAsync(ct) ?? 0) + 1,
                ModelId = VertexLitigationAiAnalysisClient.ModelId, PromptVersion = LitigationAnalysisPromptBuilder.PromptVersion };
            db.LitigationAiAnalysisRuns.Add(run);
            try { await db.SaveChangesAsync(ct); queue.Enqueue(run.LitigationAiAnalysisRunId); return run; }
            catch (DbUpdateException) { db.Entry(run).State = EntityState.Detached; active = await Active(requestId, ct); if (active is not null) return active; }
        }
        throw new InvalidOperationException("Unable to admit analysis run after concurrent updates.");
    }
    private Task<LitigationAiAnalysisRun?> Active(long id, CancellationToken ct) => db.LitigationAiAnalysisRuns.Where(x => x.RequestId == id && (x.Status == LitigationAiAnalysisRunStatus.Pending || x.Status == LitigationAiAnalysisRunStatus.InProgress)).OrderByDescending(x => x.LitigationAiAnalysisRunId).FirstOrDefaultAsync(ct);

    public async Task RunAsync(long runId, CancellationToken ct)
    {
        var token = Guid.NewGuid(); var now = DateTime.UtcNow;
        var claimed = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == runId && x.Status == LitigationAiAnalysisRunStatus.Pending && (x.NextAttemptUtc == null || x.NextAttemptUtc <= now)).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, LitigationAiAnalysisRunStatus.InProgress).SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
            .SetProperty(x => x.LeaseOwner, LeaseOwner).SetProperty(x => x.LeaseToken, token).SetProperty(x => x.LeaseExpiresUtc, now.AddSeconds(LeaseSeconds)).SetProperty(x => x.StartedUtc, now), ct);
        if (claimed == 0) return;
        try
        {
            var requestId = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == runId && x.LeaseToken == token).Select(x => x.RequestId).SingleAsync(ct);
            var cases = await db.LitigationCases.Where(x => x.RequestId == requestId).ToListAsync(ct);
            var chunks = await db.LitigationOrderChunks.Where(x => x.RequestId == requestId).ToListAsync(ct);
            var present = await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId).Select(x => x.LitigationCaseId).ToHashSetAsync(ct);
            var additions = new List<LitigationCaseAiAnalysis>();
            foreach (var c in cases.Where(x => !present.Contains(x.LitigationCaseId))) additions.Add(await AnalyzeCaseAsync(runId, token, c, chunks.Where(x => x.LitigationCaseId == c.LitigationCaseId), ct));
            await PublishCasesAsync(runId, token, additions, ct);
            var persisted = await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId).OrderBy(x => x.LitigationCaseAiAnalysisId).ToListAsync(ct);
            if (!await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == runId, ct)) await PublishPortfolioAsync(runId, token, persisted, ct);
            await CompleteAsync(runId, token, ct);
        }
        catch (LeaseLostException) { logger.LogInformation("Litigation analysis {RunId} lease lost; no stale publication permitted.", runId); }
        catch (Exception ex) { await FailOrRetryAsync(runId, token, ex, ct); }
    }

    private async Task<LitigationCaseAiAnalysis> AnalyzeCaseAsync(long runId, Guid token, LitigationCase c, IEnumerable<LitigationOrderChunk> chunks, CancellationToken ct)
    {
        var evidence = LitigationAnalysisPromptBuilder.BuildCaseEvidence(c, chunks); var json = LitigationAnalysisPromptBuilder.SerializeEvidence(evidence); var prompt = LitigationAnalysisPromptBuilder.BuildCasePrompt(evidence);
        var item = new LitigationCaseAiAnalysis { LitigationAiAnalysisRunId = runId, LitigationCaseId = c.LitigationCaseId, EvidenceJson = json, EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(json), PromptHash = LitigationAnalysisPromptBuilder.ComputeHash(prompt) };
        if (evidence.Excerpts.Count == 0) { item.Status = LitigationAiAnalysisItemStatus.InsufficientEvidence; item.AnalysisJson = "{\"status\":\"InsufficientEvidence\",\"summary\":\"No retained order text is available.\",\"unknowns\":[\"Order text unavailable\"],\"evidenceReferences\":[]}"; item.CompletedUtc = DateTime.UtcNow; return item; }
        await RenewAsync(runId, token, ct); var response = await client.CallAsync(prompt, options.Value.TimeoutSeconds, ct); await RenewAsync(runId, token, ct);
        item.RawResponseJson = response.RawResponse; item.ResponseHash = string.IsNullOrWhiteSpace(response.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(response.RawResponse);
        var valid = response.Success ? LitigationAnalysisResponseValidator.ValidateCase(response.RawResponse, evidence) : LitigationAnalysisValidationResult.Rejected(response.FailureReason ?? "Case AI call failed.");
        item.Status = valid.IsAccepted ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed; item.AnalysisJson = valid.AnalysisJson; item.FailureReason = valid.RejectReason; item.CompletedUtc = DateTime.UtcNow; return item;
    }

    private async Task RenewAsync(long id, Guid token, CancellationToken ct)
    {
        if (await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisRunStatus.InProgress && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(LeaseSeconds)), ct) != 1) throw new LeaseLostException();
    }
    private async Task PublishCasesAsync(long id, Guid token, List<LitigationCaseAiAnalysis> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return; await using var tx = await db.Database.BeginTransactionAsync(ct); await RenewAsync(id, token, ct);
        var present = await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == id).Select(x => x.LitigationCaseId).ToHashSetAsync(ct);
        db.LitigationCaseAiAnalyses.AddRange(rows.Where(x => !present.Contains(x.LitigationCaseId))); await db.SaveChangesAsync(ct); await RenewAsync(id, token, ct); await tx.CommitAsync(ct);
    }
    private async Task PublishPortfolioAsync(long id, Guid token, List<LitigationCaseAiAnalysis> rows, CancellationToken ct)
    {
        var evidence = JsonSerializer.Serialize(rows.Select(x => new { x.LitigationCaseAiAnalysisId, x.LitigationCaseId, x.Status, x.AnalysisJson, x.EvidenceHash })); var prompt = LitigationAnalysisPromptBuilder.BuildPortfolioPrompt(evidence);
        var item = new LitigationPortfolioAiAnalysis { LitigationAiAnalysisRunId = id, EvidenceJson = evidence, EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(evidence), PromptHash = LitigationAnalysisPromptBuilder.ComputeHash(prompt) };
        var usable = rows.Where(x => x.Status == LitigationAiAnalysisItemStatus.Completed).Select(x => x.LitigationCaseAiAnalysisId).ToHashSet();
        if (usable.Count == 0) { item.Status = LitigationAiAnalysisItemStatus.InsufficientEvidence; item.AnalysisJson = "{\"status\":\"InsufficientEvidence\",\"summary\":\"No completed evidence-grounded case analysis is available for synthesis.\",\"unknowns\":[\"Case analyses unavailable\"],\"caseAnalysisIds\":[]}"; }
        else { await RenewAsync(id, token, ct); var response = await client.CallAsync(prompt, options.Value.TimeoutSeconds, ct); await RenewAsync(id, token, ct); item.RawResponseJson = response.RawResponse; item.ResponseHash = string.IsNullOrWhiteSpace(response.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(response.RawResponse); var valid = response.Success ? LitigationAnalysisResponseValidator.ValidatePortfolio(response.RawResponse, usable) : LitigationAnalysisValidationResult.Rejected(response.FailureReason ?? "Portfolio AI call failed."); item.Status = valid.IsAccepted ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed; item.AnalysisJson = valid.AnalysisJson; item.FailureReason = valid.RejectReason; }
        item.CompletedUtc = DateTime.UtcNow; await using var tx = await db.Database.BeginTransactionAsync(ct); await RenewAsync(id, token, ct); if (!await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id, ct)) db.LitigationPortfolioAiAnalyses.Add(item); await db.SaveChangesAsync(ct); await RenewAsync(id, token, ct); await tx.CommitAsync(ct);
    }
    private async Task CompleteAsync(long id, Guid token, CancellationToken ct)
    {
        var bad = await db.LitigationCaseAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisItemStatus.Failed, ct) || await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisItemStatus.Failed, ct);
        if (await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisRunStatus.InProgress && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, bad ? LitigationAiAnalysisRunStatus.CompletedWithErrors : LitigationAiAnalysisRunStatus.Completed).SetProperty(x => x.CompletedUtc, DateTime.UtcNow).SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct) != 1) throw new LeaseLostException();
    }
    private async Task FailOrRetryAsync(long id, Guid token, Exception ex, CancellationToken ct)
    {
        logger.LogWarning(ex, "Litigation AI analysis {RunId} failed", id); var attempt = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.LeaseToken == token).Select(x => (int?)x.AttemptCount).SingleOrDefaultAsync(ct); if (attempt is null) return;
        var retry = attempt < options.Value.MaxAttempts; var next = DateTime.UtcNow.AddSeconds(5 * Math.Pow(3, Math.Max(0, attempt.Value - 1)));
        var updated = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, retry ? LitigationAiAnalysisRunStatus.Pending : LitigationAiAnalysisRunStatus.Failed).SetProperty(x => x.FailureReason, ex.Message).SetProperty(x => x.NextAttemptUtc, retry ? next : (DateTime?)null).SetProperty(x => x.CompletedUtc, retry ? (DateTime?)null : DateTime.UtcNow).SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct); if (updated == 1 && retry) ScheduleRetry(id, next, ct);
    }
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct) { var now = DateTime.UtcNow; await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.InProgress && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisRunStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct); var pending = await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.Pending).Select(x => new { x.LitigationAiAnalysisRunId, x.NextAttemptUtc }).ToListAsync(ct); foreach (var x in pending) if (x.NextAttemptUtc is { } due && due > now) ScheduleRetry(x.LitigationAiAnalysisRunId, due, ct); else queue.Enqueue(x.LitigationAiAnalysisRunId); return pending.Count; }
    private void ScheduleRetry(long id, DateTime due, CancellationToken ct) { var delay = due - DateTime.UtcNow; if (delay <= TimeSpan.Zero) { queue.Enqueue(id); return; } var q = queue; _ = Task.Run(async () => { try { await Task.Delay(delay, ct); q.Enqueue(id); } catch (OperationCanceledException) { } }, ct); }
    private sealed class LeaseLostException : Exception;
}
