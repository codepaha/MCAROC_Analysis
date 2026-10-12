using System.Text.Json;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MCAROC_Analysis.Services.AiUsage;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Database lease token fences every publication; a stale worker can neither persist results nor
/// complete a run after takeover. Persisted case/portfolio rows make retries idempotent.</summary>
public sealed class LitigationAiAnalysisOrchestrator(AppDbContext db, ILitigationAiAnalysisClient client,
    LitigationAiAnalysisQueue queue, IOptions<LitigationAiAnalysisOptions> options, ILogger<LitigationAiAnalysisOrchestrator> logger)
{
    private static readonly string LeaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private const int LeaseMarginSeconds = 60;
    /// <summary>The model calls of a run overlap, but the DbContext is not thread-safe: every write to it goes through this gate.</summary>
    private readonly SemaphoreSlim _dbGate = new(1, 1);
    private int LeaseSeconds => Math.Max(options.Value.TimeoutSeconds, options.Value.CaseTimeoutSeconds) + LeaseMarginSeconds;

    /// <summary>Plan §4.2/§4.2a. <paramref name="triggerSnapshotId"/> is this request's own newest
    /// fully-imported snapshot at admission time; <paramref name="originSnapshotId"/> is the snapshot this
    /// run's admission scope is actually keyed on (<c>analysis|{OriginSnapshotId}</c>) — the two differ only
    /// when the trigger snapshot was itself reused (#291), in which case <c>OriginSnapshotId</c> is copied
    /// forward from the source so N requests reusing one purchased report still resolve to the same scope.
    /// <paramref name="originSnapshotId"/> is null only for a manual start with no completed snapshot yet
    /// (still deduped in flight via the per-request <see cref="Active"/> check below, just not against the
    /// database-level "one Auto run per snapshot" index).</summary>
    public async Task<LitigationAiAnalysisRun> CreateOrJoinAsync(long requestId, LitigationAiAnalysisTrigger trigger, long? triggerSnapshotId, long? originSnapshotId, CancellationToken ct)
    {
        var active = await Active(requestId, ct); if (active is not null) return active;
        for (var i = 0; i < 3; i++)
        {
            var run = new LitigationAiAnalysisRun { RequestId = requestId, CreatedUtc = DateTime.UtcNow,
                RunNumber = (await db.LitigationAiAnalysisRuns.Where(x => x.RequestId == requestId).Select(x => (int?)x.RunNumber).MaxAsync(ct) ?? 0) + 1,
                ModelId = options.Value.CaseModelId, PromptVersion = LitigationCaseAnalysisPrompt.Version,
                Trigger = trigger, TriggerSnapshotId = triggerSnapshotId, OriginSnapshotId = originSnapshotId };
            db.LitigationAiAnalysisRuns.Add(run);
            try { await db.SaveChangesAsync(ct); queue.Enqueue(run.LitigationAiAnalysisRunId); return run; }
            catch (DbUpdateException ex)
            {
                db.Entry(run).State = EntityState.Detached;
                active = await Active(requestId, ct);
                if (active is not null) return active;
                // Another request's Auto run already claims this OriginSnapshotId (the filtered unique index,
                // plan §4.2) — the paid-call admission ledger should already have refused a second Auto start
                // for the same scope key before this is ever reached, so this is defence in depth, not the
                // primary guarantee. Join that run rather than retrying a create that will fail again; true
                // cross-request sharing of the analysed result is #291's job, not this one's.
                if (trigger == LitigationAiAnalysisTrigger.Auto && originSnapshotId is not null && IsUniqueViolation(ex)
                    && await db.LitigationAiAnalysisRuns.Where(x => x.OriginSnapshotId == originSnapshotId && x.Trigger == LitigationAiAnalysisTrigger.Auto)
                        .OrderByDescending(x => x.LitigationAiAnalysisRunId).FirstOrDefaultAsync(ct) is { } shared)
                    return shared;
            }
        }
        throw new InvalidOperationException("Unable to admit analysis run after concurrent updates.");
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };
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
            using var usageScope = AiUsageContext.Begin(requestId);
            var cases = await db.LitigationCases.Include(x => x.Orders).Where(x => x.RequestId == requestId && x.MergedIntoCaseId == null).ToListAsync(ct);
            var chunks = await LiveChunks(db, db.LitigationOrderChunks, requestId).ToListAsync(ct);
            var context = await LoadAnalysisContextAsync(requestId, ct);
            _breaker = new LitigationOutageBreaker(options.Value.OutageAfterConsecutiveFailures);
            // A retried run (after an outage, a restart or an error) tries again what failed; only finished work is skipped.
            await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId && x.Status == LitigationAiAnalysisItemStatus.Failed).ExecuteDeleteAsync(ct);
            await db.LitigationOrderClassifications.Where(x => x.LitigationAiAnalysisRunId == runId && x.Status == LitigationAiAnalysisItemStatus.Failed).ExecuteDeleteAsync(ct);
            await db.LitigationPortfolioAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId && x.Status == LitigationAiAnalysisItemStatus.Failed).ExecuteDeleteAsync(ct);
            var present = await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId).Select(x => x.LitigationCaseId).ToHashSetAsync(ct);
            // A case whose evidence is byte-identical to one already analysed is never sent to the model again:
            // its earlier result is carried into this run. Only new or changed cases cost an AI call.
            var prior = await LoadPriorAsync(requestId, runId, ct);
            var selected = SelectForAnalysis(cases, context);
            var todo = selected.Where(x => !present.Contains(x.LitigationCaseId)).ToList(); var reused = 0;
            await RunBoundedAsync(todo, options.Value.CaseConcurrency, ct, async (c, token2) =>
            {
                // One case that throws (bad data, a validator edge) fails that case only; the rest of the run carries on.
                (LitigationCaseAiAnalysis Row, bool ReusedFromAnalysis) item;
                try { item = await AnalyzeCaseAsync(runId, token, c, context, prior.GetValueOrDefault(c.LitigationCaseId), token2); }
                // Only errors a bad case's own data can cause; a database, lease or cancellation failure still stops the run, as before.
                catch (Exception ex) when (ex is JsonException or ArgumentException or FormatException or KeyNotFoundException or InvalidCastException
                    or NullReferenceException or IndexOutOfRangeException)
                {
                    logger.LogWarning(ex, "Litigation case {CaseId} could not be analysed", c.LitigationCaseId);
                    item = (UnexpectedFailure(runId, c, ex), false);
                }
                if (item.ReusedFromAnalysis) Interlocked.Increment(ref reused);
                // Saved as soon as it exists: a crash, restart or lost lease keeps everything already paid for, and a retried run skips what is present.
                await PublishCasesAsync(runId, token, [item.Row], token2);
            });
            await ClassifyOrdersAsync(runId, token, requestId, ClassifiableChunks(cases, chunks), ct);
            // A retried run may hold analyses of a case that has since left the live set (merged away): the summary is made over, and stamped with, the cases that are in the set now.
            var liveCaseIds = cases.Select(c => c.LitigationCaseId).ToHashSet();
            var persisted = (await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == runId).OrderBy(x => x.LitigationCaseAiAnalysisId).ToListAsync(ct))
                .Where(x => liveCaseIds.Contains(x.LitigationCaseId)).ToList();
            if (!await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == runId, ct))
            {
                LitigationPortfolioAiAnalysis? priorPortfolio = null;
                var caseSetHash = CaseSetHash(cases.Select(c => c.LitigationCaseId));
                if (reused == todo.Count && todo.Count > 0)
                    priorPortfolio = await db.LitigationPortfolioAiAnalyses.AsNoTracking()
                        .Where(p => p.LitigationAiAnalysisRunId != runId && p.Status == LitigationAiAnalysisItemStatus.Completed && p.CaseSetHash == caseSetHash
                            && db.LitigationAiAnalysisRuns.Any(r => r.LitigationAiAnalysisRunId == p.LitigationAiAnalysisRunId && r.RequestId == requestId))
                        .OrderByDescending(p => p.LitigationPortfolioAiAnalysisId).FirstOrDefaultAsync(ct);
                await PublishPortfolioAsync(runId, token, persisted, priorPortfolio, cases.Count, caseSetHash, ct);
            }
            await CompleteAsync(runId, token, ct);
        }
        catch (LeaseLostException) { logger.LogInformation("Litigation analysis {RunId} lease lost; no stale publication permitted.", runId); }
        catch (Exception ex) { await FailOrRetryAsync(runId, token, ex, ct); }
    }

    private async Task<Dictionary<long, List<LitigationCaseAiAnalysis>>> LoadPriorAsync(long requestId, long excludeRunId, CancellationToken ct) =>
        (await db.LitigationCaseAiAnalyses.AsNoTracking()
                .Where(x => x.LitigationAiAnalysisRunId != excludeRunId && x.CompletedUtc != null
                    && (x.Status == LitigationAiAnalysisItemStatus.Completed || x.Status == LitigationAiAnalysisItemStatus.InsufficientEvidence)
                    && db.LitigationCases.Any(c => c.LitigationCaseId == x.LitigationCaseId && c.RequestId == requestId && c.MergedIntoCaseId == null))
                .ToListAsync(ct))
            .GroupBy(x => x.LitigationCaseId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LitigationCaseAiAnalysisId).ToList());

    /// <summary>True when the request has cases and every one is already analysed for its current evidence.</summary>
    /// <summary>The request's chunks that count: those of a case that is not soft-merged and of an order that was not collapsed into another (plan 4.5, 4.6). A hidden loser's
    /// order text is the same text as the survivor's, so classifying it would be duplicate work and duplicate cost.</summary>
    /// <summary>The hash of a set of case ids, order-independent. A summary is current only for the set it was made over.</summary>
    public static string CaseSetHash(IEnumerable<long> caseIds) =>
        LitigationAnalysisPromptBuilder.ComputeHash(string.Join(",", caseIds.Distinct().OrderBy(i => i)));

    public static IQueryable<LitigationOrderChunk> LiveChunks(AppDbContext db, IQueryable<LitigationOrderChunk> chunks, long requestId) =>
        chunks.Where(x => x.RequestId == requestId
            && db.LitigationCases.Any(c => c.LitigationCaseId == x.LitigationCaseId && c.MergedIntoCaseId == null)
            && !db.LitigationCaseOrders.Any(o => o.LitigationCaseOrderId == x.LitigationCaseOrderId && o.CollapsedIntoOrderId != null));

    public async Task<bool> IsUpToDateAsync(long requestId, CancellationToken ct) =>
        await db.LitigationCases.AsNoTracking().AnyAsync(x => x.RequestId == requestId && x.MergedIntoCaseId == null, ct) && !await NeedsAnalysisAsync(requestId, ct);

    /// <summary>False when starting a run would only repeat work already done: every case's evidence matches an
    /// analysis already on file and a portfolio synthesis exists. Lets the start path refuse without spending.</summary>
    public async Task<bool> NeedsAnalysisAsync(long requestId, CancellationToken ct)
    {
        var cases = await db.LitigationCases.AsNoTracking().Include(x => x.Orders).Where(x => x.RequestId == requestId && x.MergedIntoCaseId == null).ToListAsync(ct);
        if (cases.Count == 0) return false;
        // InsufficientEvidence is a finished, deterministic portfolio outcome (no case had usable order text), so it
        // counts as "done" too; only a Pending/Failed or missing portfolio calls for another run.
        var currentSetHash = CaseSetHash(cases.Select(c => c.LitigationCaseId));
        var hasPortfolio = await db.LitigationPortfolioAiAnalyses.AsNoTracking().AnyAsync(p =>
            p.CaseSetHash == currentSetHash   // a summary over a different set of cases is not current, whatever each remaining case's hash says
            && (p.Status == LitigationAiAnalysisItemStatus.Completed || p.Status == LitigationAiAnalysisItemStatus.InsufficientEvidence)
            && db.LitigationAiAnalysisRuns.Any(r => r.LitigationAiAnalysisRunId == p.LitigationAiAnalysisRunId && r.RequestId == requestId), ct);
        if (!hasPortfolio) return true;
        var chunks = await LiveChunks(db, db.LitigationOrderChunks.AsNoTracking(), requestId).ToListAsync(ct);
        var prior = await LoadPriorAsync(requestId, 0, ct);
        var context = await LoadAnalysisContextAsync(requestId, ct);
        var casePromptHash = LitigationCaseAnalysisInputBuilder.Hash(LitigationCaseAnalysisPrompt.Instructions);
        foreach (var c in SelectForAnalysis(cases, context))
        {
            var hash = context.Build(c).EvidenceHash;
            if (!(prior.GetValueOrDefault(c.LitigationCaseId)?.Any(p => p.EvidenceHash == hash && p.PromptHash == casePromptHash) ?? false)) return true;
        }
        // An order with retained text but no outcome classification for its current evidence (e.g. analysed
        // before #195's classification existed) is also work a run would do — never report "up to date" over it.
        var priorClassifications = await LoadPriorClassificationsAsync(requestId, 0, ct);
        foreach (var document in ClassifiableChunks(cases, chunks).GroupBy(x => x.LitigationOrderDocumentId))
        {
            var (hash, promptHash) = LitigationOrderClassifier.Hashes(LitigationOrderClassifier.BuildEvidence(document.ToList()));
            if (!(priorClassifications.GetValueOrDefault(document.Key)?.Any(p => p.EvidenceHash == hash && p.PromptHash == promptHash) ?? false)) return true;
        }
        return false;
    }

    /// <summary>Epic #195: one validated outcome classification per order document with retained text, under this
    /// run's existing admission. An order whose evidence and prompt match an earlier classification is carried
    /// forward without a model call. Each row is published as soon as it exists, so a crash or lease loss part-way
    /// through never throws away a classification that was already paid for — a retried run skips what is present.</summary>
    /// <summary>Classifies every order document with chunks. Unchanged documents carry their earlier result forward;
    /// a document whose text is identical to another document's already-classified text (#350 — the same order sheet
    /// under a parent case and its IA, or an upstream duplicate) copies that result instead of a fresh model call.
    /// Only results that are current for their own document qualify as a source: a row (from this run or an earlier
    /// one) whose evidence and prompt hashes still match its document's chunks now. That includes rows this run wrote
    /// before a crash — the run resumes later, and the document may have been rechunked in between.</summary>
    private async Task ClassifyOrdersAsync(long runId, Guid token, long requestId, List<LitigationOrderChunk> chunks, CancellationToken ct)
    {
        var present = await db.LitigationOrderClassifications.AsNoTracking().Where(x => x.LitigationAiAnalysisRunId == runId)
            .ToListAsync(ct);
        var prior = await LoadPriorClassificationsAsync(requestId, runId, ct);
        var documents = chunks.GroupBy(x => x.LitigationOrderDocumentId).OrderBy(g => g.Key)
            .Select(g => (Id: g.Key, Chunks: g.ToList(), Evidence: LitigationOrderClassifier.BuildEvidence(g.ToList())))
            .ToList();

        var sameText = new Dictionary<string, LitigationOrderClassification>();
        void Offer(LitigationOrderEvidence evidence, LitigationOrderClassification row)
        {
            if (row.Status is LitigationAiAnalysisItemStatus.Completed or LitigationAiAnalysisItemStatus.InsufficientEvidence)
                sameText.TryAdd(LitigationOrderClassifier.ContentKey(evidence), row);
        }
        var presentByDocument = present.ToDictionary(x => x.LitigationOrderDocumentId);
        foreach (var d in documents)
        {
            var (evidenceHash, promptHash) = LitigationOrderClassifier.Hashes(d.Evidence);
            bool IsCurrent(LitigationOrderClassification r) => r.EvidenceHash == evidenceHash && r.PromptHash == promptHash;
            var current = presentByDocument.TryGetValue(d.Id, out var inRun)
                ? (IsCurrent(inRun) ? inRun : null)
                : prior.GetValueOrDefault(d.Id)?.FirstOrDefault(IsCurrent);
            if (current is not null)
                Offer(d.Evidence, current);
        }

        // Identical text is classified once and copied (#350). Documents with the same text therefore run one after another within their group, so the
        // second finds the first's result; different groups run in parallel. The shared "same text" map is read and written under a lock.
        var sameTextLock = new object();
        var groups = documents.Where(x => !presentByDocument.ContainsKey(x.Id)).GroupBy(x => LitigationOrderClassifier.ContentKey(x.Evidence)).ToList();
        await RunBoundedAsync(groups, options.Value.OrderConcurrency, ct, async (group, token2) =>
        {
            foreach (var d in group)
            {
                LitigationOrderClassification? source;
                lock (sameTextLock) source = sameText.GetValueOrDefault(group.Key);
                var row = await ClassifyOrderAsync(runId, token, requestId, d.Chunks, prior.GetValueOrDefault(d.Id), source, token2);
                await _dbGate.WaitAsync(token2);
                try
                {
                    await using var tx = await db.Database.BeginTransactionAsync(token2);
                    await RenewCoreAsync(runId, token, token2);
                    if (!await db.LitigationOrderClassifications.AnyAsync(x => x.LitigationAiAnalysisRunId == runId && x.LitigationOrderDocumentId == d.Id, token2))
                    {
                        db.LitigationOrderClassifications.Add(row);
                        await db.SaveChangesAsync(token2);
                        lock (sameTextLock) Offer(d.Evidence, row);
                    }
                    await tx.CommitAsync(token2);
                }
                finally { _dbGate.Release(); }
            }
        });
    }

    /// <summary>Runs <paramref name="work"/> over every item with at most <paramref name="concurrency"/> in flight. The first failure cancels the work still
    /// waiting to start (no further paid calls once the run is going to fail); calls already running are left to finish and save, and the failure is rethrown after they have.</summary>
    private static async Task RunBoundedAsync<T>(IReadOnlyList<T> items, int concurrency, CancellationToken ct, Func<T, CancellationToken, Task> work)
    {
        if (items.Count == 0) return;
        // The stop signal only keeps QUEUED items from starting. Work already running gets the caller's own token, so a sibling that is mid-call
        // still finishes and saves its result: cancelling it would throw away a paid response that has not been persisted yet.
        using var stopStarting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(stopStarting.Token);
            try
            {
                stopStarting.Token.ThrowIfCancellationRequested();   // a failure landed while this item waited for its turn
                await work(item, ct);
            }
            catch { stopStarting.Cancel(); throw; }
            finally { gate.Release(); }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch
        {
            // report the failure that started the cancellation, not the cancellations it caused
            var first = tasks.FirstOrDefault(t => t.IsFaulted) ?? tasks.First(t => t.IsFaulted || t.IsCanceled);
            if (first.IsFaulted) await first; else throw;
        }
    }

    private async Task<LitigationOrderClassification> ClassifyOrderAsync(long runId, Guid token, long requestId, List<LitigationOrderChunk> documentChunks,
        List<LitigationOrderClassification>? priorForDocument, LitigationOrderClassification? sameTextSource, CancellationToken ct)
    {
        var evidence = LitigationOrderClassifier.BuildEvidence(documentChunks);
        var json = LitigationOrderClassifier.SerializeEvidence(evidence);
        var prompt = LitigationOrderClassifier.BuildPrompt(evidence);
        var (evidenceHash, promptHash) = LitigationOrderClassifier.Hashes(evidence);
        var row = new LitigationOrderClassification
        {
            LitigationAiAnalysisRunId = runId, RequestId = requestId, LitigationCaseId = evidence.LitigationCaseId,
            LitigationCaseOrderId = evidence.LitigationCaseOrderId, LitigationOrderDocumentId = evidence.LitigationOrderDocumentId,
            EvidenceJson = json, EvidenceHash = evidenceHash, PromptHash = promptHash, EvidenceTruncated = evidence.Truncated
        };

        if (priorForDocument?.FirstOrDefault(p => p.EvidenceHash == row.EvidenceHash && p.PromptHash == row.PromptHash) is { } same)
        {
            row.Status = same.Status; row.OutcomeTypesJson = same.OutcomeTypesJson; row.FineAmount = same.FineAmount;
            row.Confidence = same.Confidence; row.RawResponseJson = same.RawResponseJson; row.ResponseHash = same.ResponseHash;
            row.ClassificationJson = same.ClassificationJson; row.CompletedUtc = DateTime.UtcNow;
            row.ReusedFromClassificationId = same.ReusedFromClassificationId;
            return row;
        }

        // #350: another document of this request holds the same text and already has a current result — the model
        // would be asked to label identical excerpts, so copy it and record where it came from.
        if (sameTextSource is not null)
        {
            row.Status = sameTextSource.Status; row.OutcomeTypesJson = sameTextSource.OutcomeTypesJson; row.FineAmount = sameTextSource.FineAmount;
            row.Confidence = sameTextSource.Confidence; row.RawResponseJson = sameTextSource.RawResponseJson; row.ResponseHash = sameTextSource.ResponseHash;
            row.ClassificationJson = sameTextSource.ClassificationJson; row.CompletedUtc = DateTime.UtcNow;
            row.ReusedFromClassificationId = sameTextSource.ReusedFromClassificationId ?? sameTextSource.LitigationOrderClassificationId;
            return row;
        }

        await RenewAsync(runId, token, ct); var response = await client.CallAsync(prompt, options.Value.TimeoutSeconds, ct); await RenewAsync(runId, token, ct);
        NoteProviderResult(response);
        row.RawResponseJson = response.RawResponse;
        row.ResponseHash = string.IsNullOrWhiteSpace(response.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(response.RawResponse);
        var result = response.Success
            ? LitigationOrderClassifier.Validate(response.RawResponse, evidence)
            : LitigationOrderClassificationResult.Rejected(response.FailureReason ?? "Order classification AI call failed.");
        row.Status = result.Status; row.OutcomeTypesJson = LitigationOrderClassifier.SerializeOutcomeTypes(result.Outcomes);
        row.FineAmount = result.FineAmount; row.Confidence = result.Confidence; row.ClassificationJson = result.ClassificationJson;
        row.FailureReason = result.RejectReason; row.CompletedUtc = DateTime.UtcNow;
        return row;
    }

    private async Task<Dictionary<long, List<LitigationOrderClassification>>> LoadPriorClassificationsAsync(long requestId, long excludeRunId, CancellationToken ct) =>
        (await db.LitigationOrderClassifications.AsNoTracking()
                .Where(x => x.RequestId == requestId && x.LitigationAiAnalysisRunId != excludeRunId && x.CompletedUtc != null
                    && (x.Status == LitigationAiAnalysisItemStatus.Completed || x.Status == LitigationAiAnalysisItemStatus.InsufficientEvidence))
                .ToListAsync(ct))
            .GroupBy(x => x.LitigationOrderDocumentId).ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.LitigationOrderClassificationId).ToList());

    /// <summary>What the v2.0 case analysis needs besides the case: every order document's text, and who the target is (the company's
    /// name, the earlier names its keywords carry, and the record-based baseline tier that its side of each case feeds).</summary>
    public sealed class AnalysisContext(IReadOnlyDictionary<long, LitigationOrderDocument> documentsByOrderId, string targetEntity, IReadOnlyList<string> targetNames, DateOnly asOf)
    {
        public IReadOnlyList<string> TargetNames => targetNames;
        /// <summary>The input for the whole case, or for one group of its orders. A group is built against the whole case's input
        /// (<paramref name="whole"/>) so that it sends exactly the text the case's evidence hash covers.</summary>
        public CaseAnalysisInput Build(LitigationCase c, IReadOnlySet<long>? onlyOrderIds = null, CaseAnalysisInput? whole = null) =>
            LitigationCaseAnalysisInputBuilder.Build(c, documentsByOrderId, targetEntity, targetNames, asOf, onlyOrderIds,
                whole?.Orders.Where(o => o.Text is null && o.TotalChars > 0).Select(o => o.OrderId).ToHashSet());

        /// <summary>How worthwhile a full AI reading of the case is (see <see cref="LitigationCasePriority"/>).</summary>
        public LitigationCasePriorityResult Priority(LitigationCase c)
        {
            var petitioners = LitigationPartyNames.Parse(c.PetitionersJson);
            var side = LitigationCompanySides.Determine(petitioners, LitigationPartyNames.Parse(c.RespondentsJson), targetNames, c.Direction);
            return LitigationCasePriority.Evaluate(new LitigationRiskInput(c.Type, c.Court, c.CourtCategory, c.CaseType, c.Act, c.CaseStage,
                c.CaseClassification, c.ProceedingType, LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage), side, petitioners));
        }

        /// <summary>The tier and trigger the case's own record gives it, which the model may confirm or raise but not lower.</summary>
        public LitigationRiskAssessment Baseline(LitigationCase c)
        {
            var petitioners = LitigationPartyNames.Parse(c.PetitionersJson);
            var side = LitigationCompanySides.Determine(petitioners, LitigationPartyNames.Parse(c.RespondentsJson), targetNames, c.Direction);
            return LitigationBaselineRisk.Assess(new LitigationRiskInput(c.Type, c.Court, c.CourtCategory, c.CaseType, c.Act, c.CaseStage,
                c.CaseClassification, c.ProceedingType, LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage), side, petitioners));
        }
    }

    private async Task<AnalysisContext> LoadAnalysisContextAsync(long requestId, CancellationToken ct)
    {
        var request = await db.Requests.AsNoTracking().Where(r => r.RequestId == requestId).Select(r => r.CompanyName).SingleAsync(ct);
        var keywordJson = await db.LitigationSearchJobs.AsNoTracking().Where(j => j.RequestId == requestId).Select(j => j.KeywordsJson).ToListAsync(ct);
        var names = new List<string> { request };
        foreach (var json in keywordJson)
            foreach (var k in LitigationKeyword.ParseJson(json))
                if (!names.Contains(k.Value, StringComparer.OrdinalIgnoreCase)) names.Add(k.Value);
        var documents = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => d.Order!.Case!.RequestId == requestId && d.ExtractedText != null && d.ExtractedText != "")
            .ToListAsync(ct);
        return new AnalysisContext(documents.GroupBy(d => d.LitigationCaseOrderId).ToDictionary(g => g.Key, g => g.OrderByDescending(d => d.LitigationOrderDocumentId).First()),
            request, names, DateOnly.FromDateTime(DateTime.UtcNow));
    }

    private LitigationOutageBreaker _breaker = new(10);

    /// <summary>Notes whether a model call got a reply. Enough failed calls in a row mean the service is down: stop and let the run wait.</summary>
    private void NoteProviderResult(LitigationAiCallResult response)
    {
        _breaker.Record(!response.Success);
        if (_breaker.Tripped)
            throw new ProviderUnavailableException($"The AI service stopped answering ({_breaker.ConsecutiveFailures} calls in a row failed). "
                + $"The run pauses and retries in {options.Value.OutageRetryMinutes} minutes; everything already analysed is kept.");
    }

    private static LitigationCaseAiAnalysis UnexpectedFailure(long runId, LitigationCase c, Exception ex) => new()
    {
        LitigationAiAnalysisRunId = runId, LitigationCaseId = c.LitigationCaseId, Status = LitigationAiAnalysisItemStatus.Failed,
        EvidenceJson = "{}", EvidenceHash = "", PromptHash = "",
        FailureReason = $"Unexpected error while analysing this case ({ex.GetType().Name}).", CompletedUtc = DateTime.UtcNow
    };

    private async Task<(LitigationCaseAiAnalysis Row, bool ReusedFromAnalysis)> AnalyzeCaseAsync(long runId, Guid token, LitigationCase c, AnalysisContext context, List<LitigationCaseAiAnalysis>? priorForCase, CancellationToken ct)
    {
        var input = context.Build(c);
        var casePromptHash = LitigationCaseAnalysisInputBuilder.Hash(LitigationCaseAnalysisPrompt.Instructions);
        if (priorForCase?.FirstOrDefault(p => p.EvidenceHash == input.EvidenceHash && p.PromptHash == casePromptHash) is { } same)
            return (new LitigationCaseAiAnalysis { LitigationAiAnalysisRunId = runId, LitigationCaseId = c.LitigationCaseId, EvidenceJson = input.EvidenceJson, EvidenceHash = input.EvidenceHash, PromptHash = casePromptHash,
                RawResponseJson = same.RawResponseJson, ResponseHash = same.ResponseHash, Status = same.Status, AnalysisJson = same.AnalysisJson, CompletedUtc = DateTime.UtcNow }, true);
        return (await AnalyzeCaseFreshAsync(runId, token, c, context, input, casePromptHash, ct), false);
    }

    private const int MalformedReplyRetries = 1;

    private async Task<LitigationCaseAiAnalysis> AnalyzeCaseFreshAsync(long runId, Guid token, LitigationCase c, AnalysisContext context, CaseAnalysisInput input, string promptHash, CancellationToken ct)
    {
        var item = new LitigationCaseAiAnalysis { LitigationAiAnalysisRunId = runId, LitigationCaseId = c.LitigationCaseId, EvidenceJson = input.EvidenceJson, EvidenceHash = input.EvidenceHash, PromptHash = promptHash };
        var baseline = context.Baseline(c);
        var groups = LitigationCaseAnalysisGroups.Plan(input, options.Value.SplitAboveChars, options.Value.GroupChars);
        var raws = new List<string>(); var accepted = new List<string>(); CaseAnalysisValidation? valid = null;
        foreach (var ids in groups)
        {
            // One group is the whole case; otherwise each group is built and checked against its own orders' text.
            var part = groups.Count == 1 ? input : context.Build(c, ids, input);
            await RenewAsync(runId, token, ct);
            // A small model now and then returns JSON that cannot be read (a repeated field name, a stray character). The same call usually
            // comes back clean, so a malformed reply is asked for once more before the case is marked failed.
            LitigationAiCallResult response; CaseAnalysisValidation v;
            for (var attempt = 0; ; attempt++)
            {
                response = await client.CallAsync(LitigationCaseAnalysisPrompt.Build(part.CaseJson), options.Value.CaseTimeoutSeconds, ct, options.Value.CaseModelId);
                await RenewAsync(runId, token, ct);
                NoteProviderResult(response);
                v = response.Success ? LitigationCaseAnalysisValidator.Validate(response.RawResponse, part, baseline.Tier, baseline.Trigger)
                    : new CaseAnalysisValidation(false, null, response.FailureReason ?? "Case AI call failed.", []);
                if (v.IsAccepted || !response.Success || attempt >= MalformedReplyRetries || !LitigationCaseAnalysisValidator.IsMalformed(v.RejectReason)) break;
            }
            raws.Add(response.RawResponse);
            if (!v.IsAccepted)
            {
                valid = new CaseAnalysisValidation(false, null, groups.Count == 1 ? v.RejectReason : $"Group {raws.Count} of {groups.Count}: {v.RejectReason}", []);
                break;
            }
            accepted.Add(v.AnalysisJson!);
        }
        valid ??= new CaseAnalysisValidation(true, LitigationCaseAnalysisGroups.Merge(accepted), null, []);
        var raw = groups.Count == 1 ? raws[^1] : string.Join("\n", raws.Select((r, i) => $"/* group {i + 1} of {groups.Count} */ {r}"));
        item.RawResponseJson = raw; item.ResponseHash = string.IsNullOrWhiteSpace(raw) ? null : LitigationCaseAnalysisInputBuilder.Hash(raw);
        item.Status = valid.IsAccepted ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed; item.AnalysisJson = valid.AnalysisJson; item.FailureReason = valid.RejectReason; item.CompletedUtc = DateTime.UtcNow;
        return item;
    }

    private async Task RenewAsync(long id, Guid token, CancellationToken ct)
    {
        await _dbGate.WaitAsync(ct);
        try { await RenewCoreAsync(id, token, ct); }
        finally { _dbGate.Release(); }
    }

    /// <summary>Renews the lease; the caller already holds <see cref="_dbGate"/>.</summary>
    private async Task RenewCoreAsync(long id, Guid token, CancellationToken ct)
    {
        if (await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisRunStatus.InProgress && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresUtc, DateTime.UtcNow.AddSeconds(LeaseSeconds)), ct) != 1) throw new LeaseLostException();
    }
    private async Task PublishCasesAsync(long id, Guid token, List<LitigationCaseAiAnalysis> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        await _dbGate.WaitAsync(ct);
        try
        {
        await using var tx = await db.Database.BeginTransactionAsync(ct); await RenewCoreAsync(id, token, ct);
        var present = await db.LitigationCaseAiAnalyses.Where(x => x.LitigationAiAnalysisRunId == id).Select(x => x.LitigationCaseId).ToHashSetAsync(ct);
        db.LitigationCaseAiAnalyses.AddRange(rows.Where(x => !present.Contains(x.LitigationCaseId))); await db.SaveChangesAsync(ct); await RenewCoreAsync(id, token, ct); await tx.CommitAsync(ct);
        }
        finally { _dbGate.Release(); }
    }
    /// <summary>The order chunks to classify. Every order for a company at or below <c>TieringAboveCases</c>; above it, only the judgments
    /// (<see cref="LitigationJudgmentFilter"/>) - the orders where the outcome is - since classifying every adjournment of thousands of cases
    /// would take hours for labels nobody reads. The same set decides whether a run still has work, so skipped orders never keep a run alive.</summary>
    internal List<LitigationOrderChunk> ClassifiableChunks(IReadOnlyList<LitigationCase> cases, List<LitigationOrderChunk> chunks)
    {
        if (cases.Count <= options.Value.TieringAboveCases) return chunks;
        var status = cases.ToDictionary(c => c.LitigationCaseId, c => LitigationCaseStatusClassifier.Classify(c.CaseStatus, c.CaseStage));
        var judgments = LitigationJudgmentFilter.JudgmentDocumentIds(chunks, status);
        return chunks.Where(c => judgments.Contains(c.LitigationOrderDocumentId)).ToList();
    }

    /// <summary>The cases to read in full. Every case up to <c>TieringAboveCases</c>; beyond that, the tier-A cases by score (insolvency first),
    /// at most <c>MaxAiCases</c>. The same choice is used to decide whether a run still has work, so cases left unread never keep a run alive.</summary>
    internal IReadOnlyList<LitigationCase> SelectForAnalysis(IReadOnlyList<LitigationCase> cases, AnalysisContext context)
    {
        if (cases.Count <= options.Value.TieringAboveCases) return cases;
        return cases
            .Select(c => (Case: c, Priority: context.Priority(c)))
            .Where(x => x.Priority.Priority == LitigationAnalysisPriority.A)
            .OrderByDescending(x => x.Priority.Score).ThenBy(x => x.Case.LitigationCaseId)
            .Take(options.Value.MaxAiCases)
            .Select(x => x.Case).ToList();
    }

    private async Task PublishPortfolioAsync(long id, Guid token, List<LitigationCaseAiAnalysis> rows, LitigationPortfolioAiAnalysis? reusable, int totalCases, string caseSetHash, CancellationToken ct)
    {
        // Up to one batch of analysed cases, with every case read, the synthesis is the single prompt it always was. More than that is summarised
        // in small batches (LitigationPortfolioSynthesizer) that each read the cases together with their order quotes, then combined; and when
        // not every case was read, the synthesis is told how many cases exist.
        var batched = rows.Count(x => x.Status == LitigationAiAnalysisItemStatus.Completed) > Math.Max(2, options.Value.PortfolioBatchSize);
        string evidence, prompt;
        if (rows.Count >= totalCases || batched)
        {
            evidence = JsonSerializer.Serialize(rows.Select(x => new { x.LitigationCaseAiAnalysisId, x.LitigationCaseId, x.Status, AnalysisJson = LitigationCaseAnalysisProjection.ForPortfolio(x.AnalysisJson), x.EvidenceHash }));
            prompt = LitigationAnalysisPromptBuilder.BuildPortfolioPrompt(evidence);
        }
        else
        {
            var coverage = new { totalCases, casesShown = rows.Count,
                note = "Cases not analysed were ranked lower by a fixed rule (the company as claimant, no financial act) and keep their court-record risk tier; they are not negative findings." };
            evidence = JsonSerializer.Serialize(new { coverage, analyses = rows.Select(x => new { x.LitigationCaseAiAnalysisId, x.LitigationCaseId, x.Status, AnalysisJson = LitigationCaseAnalysisProjection.ForPortfolio(x.AnalysisJson), x.EvidenceHash }) });
            prompt = LitigationAnalysisPromptBuilder.BuildPortfolioPrompt(evidence, withCoverage: true);
        }
        var item = new LitigationPortfolioAiAnalysis { LitigationAiAnalysisRunId = id, CaseSetHash = caseSetHash, EvidenceJson = evidence, EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(evidence), PromptHash = LitigationAnalysisPromptBuilder.ComputeHash(prompt) };
        var usable = rows.Where(x => x.Status == LitigationAiAnalysisItemStatus.Completed).Select(x => x.LitigationCaseAiAnalysisId).ToHashSet();
        if (reusable is not null)
        {
            // Every case result was carried over unchanged, so the earlier portfolio synthesis still holds.
            item.RawResponseJson = reusable.RawResponseJson; item.ResponseHash = reusable.ResponseHash; item.Status = reusable.Status; item.AnalysisJson = reusable.AnalysisJson;
        }
        else if (batched)
        {
            var result = await new LitigationPortfolioSynthesizer(client, options.Value).RunAsync(rows, totalCases, () => RenewAsync(id, token, ct), ct);
            item.EvidenceJson = result.Evidence; item.EvidenceHash = LitigationAnalysisPromptBuilder.ComputeHash(result.Evidence); item.PromptHash = result.PromptKey;
            item.RawResponseJson = result.RawResponse; item.ResponseHash = string.IsNullOrWhiteSpace(result.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(result.RawResponse);
            item.Status = result.Success ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed; item.AnalysisJson = result.AnalysisJson; item.FailureReason = result.FailureReason;
        }
        else if (usable.Count == 0) { item.Status = LitigationAiAnalysisItemStatus.InsufficientEvidence; item.AnalysisJson = "{\"status\":\"InsufficientEvidence\",\"summary\":\"No completed evidence-grounded case analysis is available for synthesis.\",\"unknowns\":[\"Case analyses unavailable\"],\"caseAnalysisIds\":[]}"; }
        else { await RenewAsync(id, token, ct); var response = await client.CallAsync(prompt, options.Value.TimeoutSeconds, ct); await RenewAsync(id, token, ct); item.RawResponseJson = response.RawResponse; item.ResponseHash = string.IsNullOrWhiteSpace(response.RawResponse) ? null : LitigationAnalysisPromptBuilder.ComputeHash(response.RawResponse); var valid = response.Success ? LitigationAnalysisResponseValidator.ValidatePortfolio(response.RawResponse, usable) : LitigationAnalysisValidationResult.Rejected(response.FailureReason ?? "Portfolio AI call failed."); item.Status = valid.IsAccepted ? LitigationAiAnalysisItemStatus.Completed : LitigationAiAnalysisItemStatus.Failed; item.AnalysisJson = valid.AnalysisJson; item.FailureReason = valid.RejectReason; }
        item.CompletedUtc = DateTime.UtcNow; await using var tx = await db.Database.BeginTransactionAsync(ct); await RenewAsync(id, token, ct); if (!await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id, ct)) db.LitigationPortfolioAiAnalyses.Add(item); await db.SaveChangesAsync(ct); await RenewAsync(id, token, ct); await tx.CommitAsync(ct);
    }
    private async Task CompleteAsync(long id, Guid token, CancellationToken ct)
    {
        var bad = await db.LitigationCaseAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisItemStatus.Failed, ct) || await db.LitigationPortfolioAiAnalyses.AnyAsync(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisItemStatus.Failed, ct)
            || await db.LitigationOrderClassifications.AnyAsync(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisItemStatus.Failed, ct);
        if (await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.Status == LitigationAiAnalysisRunStatus.InProgress && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, bad ? LitigationAiAnalysisRunStatus.CompletedWithErrors : LitigationAiAnalysisRunStatus.Completed).SetProperty(x => x.CompletedUtc, DateTime.UtcNow).SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct) != 1) throw new LeaseLostException();
    }
    private async Task FailOrRetryAsync(long id, Guid token, Exception ex, CancellationToken ct)
    {
        logger.LogWarning(ex, "Litigation AI analysis {RunId} failed", id); var attempt = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.LeaseToken == token).Select(x => (int?)x.AttemptCount).SingleOrDefaultAsync(ct); if (attempt is null) return;
        // An outage waits longer and gets more tries than an ordinary error: the work done so far is kept, so waiting costs nothing.
        var outage = ex is ProviderUnavailableException;
        var retry = attempt < (outage ? options.Value.OutageMaxAttempts : options.Value.MaxAttempts);
        var next = outage ? DateTime.UtcNow.AddMinutes(options.Value.OutageRetryMinutes) : DateTime.UtcNow.AddSeconds(5 * Math.Pow(3, Math.Max(0, attempt.Value - 1)));
        var updated = await db.LitigationAiAnalysisRuns.Where(x => x.LitigationAiAnalysisRunId == id && x.LeaseToken == token).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, retry ? LitigationAiAnalysisRunStatus.Pending : LitigationAiAnalysisRunStatus.Failed).SetProperty(x => x.FailureReason, ex.Message).SetProperty(x => x.NextAttemptUtc, retry ? next : (DateTime?)null).SetProperty(x => x.CompletedUtc, retry ? (DateTime?)null : DateTime.UtcNow).SetProperty(x => x.LeaseToken, (Guid?)null).SetProperty(x => x.LeaseExpiresUtc, (DateTime?)null), ct); if (updated == 1 && retry) ScheduleRetry(id, next, ct);
    }
    public async Task<int> RecoverStaleWorkAsync(CancellationToken ct) { var now = DateTime.UtcNow; await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.InProgress && (x.LeaseExpiresUtc == null || x.LeaseExpiresUtc < now)).ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, LitigationAiAnalysisRunStatus.Pending).SetProperty(x => x.LeaseToken, (Guid?)null), ct); var pending = await db.LitigationAiAnalysisRuns.Where(x => x.Status == LitigationAiAnalysisRunStatus.Pending).Select(x => new { x.LitigationAiAnalysisRunId, x.NextAttemptUtc }).ToListAsync(ct); foreach (var x in pending) if (x.NextAttemptUtc is { } due && due > now) ScheduleRetry(x.LitigationAiAnalysisRunId, due, ct); else queue.Enqueue(x.LitigationAiAnalysisRunId); return pending.Count; }
    private void ScheduleRetry(long id, DateTime due, CancellationToken ct) { var delay = due - DateTime.UtcNow; if (delay <= TimeSpan.Zero) { queue.Enqueue(id); return; } var q = queue; _ = Task.Run(async () => { try { await Task.Delay(delay, ct); q.Enqueue(id); } catch (OperationCanceledException) { } }, ct); }
    private sealed class LeaseLostException : Exception;
}
