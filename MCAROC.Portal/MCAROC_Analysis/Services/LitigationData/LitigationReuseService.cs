using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.LitigationData;

/// <param name="Reused">A source was found within the window and copied — <see cref="JobId"/>/<see
/// cref="SnapshotId"/> are the new, request-scoped copies (never the source's own ids).</param>
public sealed record LitigationReuseResult(bool Reused, long? JobId, long? SnapshotId, long? SourceRequestId, DateTime? SourceRetrievedUtc)
{
    public static readonly LitigationReuseResult None = new(false, null, null, null, null);
}

/// <summary>Plan §4.2a/#291: when another request — any client — already holds a litigation report for the
/// same scope, retrieved within the last 7 days, this request takes that report instead of buying and
/// analysing again. <see cref="TryReuseAsync"/> is the only entry point; called from
/// <see cref="LitigationStartService"/> before it would otherwise admit a fresh purchase, using the exact
/// same scope key so the two can never drift.
///
/// <b>Copy-on-reuse, not a shared reference:</b> every existing UI/query stays request-scoped and unchanged —
/// this writes a full, independent copy (job, snapshot, cases, orders, documents incl. the retained PDF file,
/// chunks incl. their embeddings, and — if present — the AI analysis) rather than teaching every reader to
/// resolve "borrowed" rows. Nothing here spends a credit or calls Vertex/BPR; it is pure database and file
/// I/O, which is exactly why it costs nothing to the reusing request.</summary>
public sealed class LitigationReuseService(
    AppDbContext db, IWebHostEnvironment env, TimeProvider time, LitigationOrderDocumentQueue documentQueue, ILogger<LitigationReuseService> logger)
{
    private const int ReuseWindowDays = 7;

    /// <summary>Looks for a reusable source and copies it if found. Never throws for "no source exists" —
    /// that is <see cref="LitigationReuseResult.None"/>, the caller's cue to proceed with a fresh purchase.
    /// Idempotent: a retried or racing call for the same request and the same ultimate origin joins the copy
    /// already made rather than duplicating it.</summary>
    public async Task<LitigationReuseResult> TryReuseAsync(long requestId, string searchScopeKey, CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-ReuseWindowDays);
        var source = await (
            from a in db.PaidCallAdmissions.AsNoTracking()
            where a.Kind == PaidCallKind.LitigationSearch && a.ScopeKey == searchScopeKey
                && a.State == PaidCallAdmissionState.Committed && a.RequestId != requestId
            join j in db.LitigationSearchJobs.AsNoTracking() on a.ReferenceId equals j.LitigationSearchJobId
            join s in db.LitigationReportSnapshots.AsNoTracking() on j.LitigationSearchJobId equals s.LitigationSearchJobId
            where s.Status == LitigationReportSnapshotStatus.Completed && s.RetrievedUtc >= cutoff
            orderby s.RetrievedUtc descending
            select s.LitigationReportSnapshotId
        ).FirstOrDefaultAsync(ct);
        if (source == 0) return LitigationReuseResult.None;

        return await CopyAsync(requestId, source, ct);
    }

    private async Task<LitigationReuseResult> CopyAsync(long requestId, long sourceSnapshotId, CancellationToken ct)
    {
        var source = await db.LitigationReportSnapshots.AsNoTracking()
            .FirstOrDefaultAsync(s => s.LitigationReportSnapshotId == sourceSnapshotId, ct);
        if (source is null) return LitigationReuseResult.None; // vanished mid-flight — caller falls through to a fresh purchase
        var sourceJob = await db.LitigationSearchJobs.AsNoTracking()
            .FirstAsync(j => j.LitigationSearchJobId == source.LitigationSearchJobId, ct);
        var originId = source.OriginSnapshotId ?? source.LitigationReportSnapshotId;

        // Idempotency check before touching anything: a prior attempt (this request, this origin) already
        // exists — join it rather than risk a partial second copy. The unique index below is the real
        // guarantee under concurrency; this is the fast, common-case path.
        var existing = await db.LitigationReportSnapshots.AsNoTracking()
            .Where(s => s.RequestId == requestId && (s.OriginSnapshotId ?? s.LitigationReportSnapshotId) == originId)
            .Select(s => new { s.LitigationReportSnapshotId, s.LitigationSearchJobId }).FirstOrDefaultAsync(ct);
        if (existing is not null)
            return new LitigationReuseResult(true, existing.LitigationSearchJobId, existing.LitigationReportSnapshotId, source.RequestId, source.RetrievedUtc);

        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var job = new LitigationSearchJob
        {
            RequestId = requestId, Status = LitigationSearchJobStatus.Completed,
            KeywordsJson = sourceJob.KeywordsJson, EntityType = sourceJob.EntityType, ApplicationCustomerId = sourceJob.ApplicationCustomerId,
            ReportFormat = sourceJob.ReportFormat, CreatedUtc = now, CompletedUtc = now
        };
        db.LitigationSearchJobs.Add(job);
        await db.SaveChangesAsync(ct);

        var snapshot = new LitigationReportSnapshot
        {
            LitigationSearchJobId = job.LitigationSearchJobId, RequestId = requestId,
            ReportHash = source.ReportHash, ReportFormat = source.ReportFormat,
            RawReportBytes = source.RawReportBytes, RawReportByteLength = source.RawReportByteLength,
            RetrievedUtc = source.RetrievedUtc, // never reset — the disclosed age is the source's real age
            Status = LitigationReportSnapshotStatus.Completed, CasesPersistedCount = source.CasesPersistedCount,
            ReusedFromSnapshotId = source.LitigationReportSnapshotId, ReusedFromRequestId = source.RequestId,
            OriginSnapshotId = originId, CreatedUtc = now, CompletedUtc = now
        };
        db.LitigationReportSnapshots.Add(snapshot);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Lost a race to another copy of the same origin for this same request — join it, not a bug.
            await tx.RollbackAsync(ct);
            var joined = await db.LitigationReportSnapshots.AsNoTracking()
                .Where(s => s.RequestId == requestId && (s.OriginSnapshotId ?? s.LitigationReportSnapshotId) == originId)
                .Select(s => new { s.LitigationReportSnapshotId, s.LitigationSearchJobId }).FirstAsync(ct);
            return new LitigationReuseResult(true, joined.LitigationSearchJobId, joined.LitigationReportSnapshotId, source.RequestId, source.RetrievedUtc);
        }

        var caseIdMap = await CopyCasesAsync(source.RequestId, sourceSnapshotId, requestId, snapshot.LitigationReportSnapshotId, ct);
        var orderIdMap = await CopyOrdersAsync(caseIdMap, ct);
        var (toEnqueue, docIdMap) = await CopyOrderDocumentsAndChunksAsync(source.RequestId, requestId, orderIdMap, caseIdMap, ct);
        await CopyAnalysisAsync(sourceSnapshotId, requestId, snapshot.LitigationReportSnapshotId, originId, caseIdMap, orderIdMap, docIdMap, ct);

        await tx.CommitAsync(ct);
        // Only now that the copies are actually committed — the worker reads through a fresh connection and
        // would silently find nothing (READ COMMITTED) for an id enqueued before commit.
        foreach (var id in toEnqueue) documentQueue.Enqueue(id);
        logger.LogInformation(
            "Litigation report reused: request {RequestId} copied snapshot {SourceSnapshotId} from request {SourceRequestId} (retrieved {RetrievedUtc}) into new snapshot {NewSnapshotId}.",
            requestId, sourceSnapshotId, source.RequestId, source.RetrievedUtc, snapshot.LitigationReportSnapshotId);
        return new LitigationReuseResult(true, job.LitigationSearchJobId, snapshot.LitigationReportSnapshotId, source.RequestId, source.RetrievedUtc);
    }

    private async Task<Dictionary<long, long>> CopyCasesAsync(long sourceRequestId, long sourceSnapshotId, long requestId, long newSnapshotId, CancellationToken ct)
    {
        var sourceCaseIds = await db.LitigationCaseSourceReports.AsNoTracking()
            .Where(r => r.LitigationReportSnapshotId == sourceSnapshotId).Select(r => r.LitigationCaseId).ToListAsync(ct);
        var sourceCases = await db.LitigationCases.AsNoTracking()
            .Where(c => c.RequestId == sourceRequestId && sourceCaseIds.Contains(c.LitigationCaseId)).ToListAsync(ct);

        var map = new Dictionary<long, long>();
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var c in sourceCases)
        {
            var copy = new LitigationCase
            {
                RequestId = requestId, ProviderCaseId = c.ProviderCaseId, CspId = c.CspId, Cnr = c.Cnr, ProceedingType = c.ProceedingType,
                CourtCategory = c.CourtCategory, Direction = c.Direction, CaseClassification = c.CaseClassification, Type = c.Type,
                Court = c.Court, Bench = c.Bench, CaseNumber = c.CaseNumber, CaseType = c.CaseType, CaseYear = c.CaseYear,
                CaseStage = c.CaseStage, CaseStatus = c.CaseStatus, Act = c.Act, FilingDate = c.FilingDate, LastHearingDate = c.LastHearingDate,
                NextHearingDate = c.NextHearingDate, DecisionDate = c.DecisionDate, State = c.State, District = c.District,
                PetitionersJson = c.PetitionersJson, RespondentsJson = c.RespondentsJson, PetitionerAdvocatesJson = c.PetitionerAdvocatesJson,
                RespondentAdvocatesJson = c.RespondentAdvocatesJson, FirstSeenUtc = c.FirstSeenUtc, LastSeenUtc = c.LastSeenUtc
            };
            db.LitigationCases.Add(copy);
            await db.SaveChangesAsync(ct);
            map[c.LitigationCaseId] = copy.LitigationCaseId;

            db.LitigationCaseSourceReports.Add(new LitigationCaseSourceReport
            {
                LitigationCaseId = copy.LitigationCaseId, LitigationReportSnapshotId = newSnapshotId,
                ProviderCaseId = c.ProviderCaseId, CspId = c.CspId, FirstSeenUtc = now
            });
        }
        await db.SaveChangesAsync(ct);
        return map;
    }

    private async Task<Dictionary<long, long>> CopyOrdersAsync(Dictionary<long, long> caseIdMap, CancellationToken ct)
    {
        var map = new Dictionary<long, long>();
        if (caseIdMap.Count == 0) return map;
        var sourceOrders = await db.LitigationCaseOrders.AsNoTracking()
            .Where(o => caseIdMap.Keys.Contains(o.LitigationCaseId)).ToListAsync(ct);
        foreach (var o in sourceOrders)
        {
            var copy = new LitigationCaseOrder
            {
                LitigationCaseId = caseIdMap[o.LitigationCaseId], PdfUrl = o.PdfUrl, OrderDate = o.OrderDate,
                OrderType = o.OrderType, CreatedUtc = o.CreatedUtc
            };
            db.LitigationCaseOrders.Add(copy);
            await db.SaveChangesAsync(ct);
            map[o.LitigationCaseOrderId] = copy.LitigationCaseOrderId;
        }
        return map;
    }

    /// <summary>Documents already <see cref="LitigationOrderDocumentStatus.Downloaded"/> get their retained
    /// PDF file-copied too — everything else (<c>Failed</c>/<c>Expired</c>/<c>Pending</c>) copies its status
    /// and reasoning verbatim. A copied row that is not terminal (<c>Pending</c>, <c>Failed</c>, or an
    /// orphaned <c>InProgress</c> — the copy never carries the source's lease fields, so it always reads as
    /// orphaned) needs its id handed back to the caller to enqueue once the copy actually commits: the
    /// in-process <see cref="LitigationOrderDocumentQueue"/> has no idea a new row exists otherwise, and would
    /// otherwise sit untouched until the next process restart's recovery sweep.</summary>
    private async Task<(List<long> ToEnqueue, Dictionary<long, long> DocIdMap)> CopyOrderDocumentsAndChunksAsync(long sourceRequestId, long requestId,
        Dictionary<long, long> orderIdMap, Dictionary<long, long> caseIdMap, CancellationToken ct)
    {
        var toEnqueue = new List<long>();
        var docIdMap = new Dictionary<long, long>();
        if (orderIdMap.Count == 0) return (toEnqueue, docIdMap);
        var sourceDocs = await db.LitigationOrderDocuments.AsNoTracking()
            .Where(d => orderIdMap.Keys.Contains(d.LitigationCaseOrderId)).ToListAsync(ct);
        foreach (var d in sourceDocs)
        {
            var copy = new LitigationOrderDocument
            {
                LitigationCaseOrderId = orderIdMap[d.LitigationCaseOrderId], Status = d.Status,
                RetainedUntilUtc = d.RetainedUntilUtc, FileSizeBytes = d.FileSizeBytes, FileHash = d.FileHash,
                ContentType = d.ContentType, DownloadedUtc = d.DownloadedUtc, FailureReason = d.FailureReason,
                ExtractedText = d.ExtractedText, TextExtractionStatus = d.TextExtractionStatus,
                TextExtractionMethod = d.TextExtractionMethod, ExtractedUtc = d.ExtractedUtc, CreatedUtc = d.CreatedUtc
            };
            db.LitigationOrderDocuments.Add(copy);
            await db.SaveChangesAsync(ct);
            docIdMap[d.LitigationOrderDocumentId] = copy.LitigationOrderDocumentId;
            if (!copy.IsTerminal) toEnqueue.Add(copy.LitigationOrderDocumentId);

            if (d.Status == LitigationOrderDocumentStatus.Downloaded && !string.IsNullOrWhiteSpace(d.StoragePath))
            {
                var newPath = await CopyRetainedFileAsync(requestId, copy.LitigationOrderDocumentId, d.StoragePath!, ct);
                if (newPath is not null)
                    await db.LitigationOrderDocuments.Where(x => x.LitigationOrderDocumentId == copy.LitigationOrderDocumentId)
                        .ExecuteUpdateAsync(s => s.SetProperty(x => x.StoragePath, newPath), ct);
                else
                    // The source file is gone (disk cleanup, moved retention) — the row still correctly says
                    // Downloaded with a hash/size for audit, but there is no file for this copy to serve. A
                    // future issue can decide whether that should instead flag the copy; not silently
                    // fabricating a wrong path is the safe default today.
                    logger.LogWarning("Litigation reuse: source order document {Id}'s retained file was missing at {Path}; copied the row without a file.", d.LitigationOrderDocumentId, d.StoragePath);
            }
        }

        // Scoped to the source request, like every other read here: a chunk is request-owned, and a document id alone
        // must never pull in another request's rows (found when a test seeded chunks with made-up document ids).
        var sourceChunks = await db.LitigationOrderChunks.AsNoTracking()
            .Where(c => c.RequestId == sourceRequestId && docIdMap.Keys.Contains(c.LitigationOrderDocumentId)).ToListAsync(ct);
        foreach (var c in sourceChunks)
            db.LitigationOrderChunks.Add(new LitigationOrderChunk
            {
                RequestId = requestId, LitigationOrderDocumentId = docIdMap[c.LitigationOrderDocumentId],
                LitigationCaseOrderId = orderIdMap[c.LitigationCaseOrderId], LitigationCaseId = caseIdMap[c.LitigationCaseId],
                CaseNumber = c.CaseNumber, Cnr = c.Cnr, Court = c.Court, OrderType = c.OrderType, OrderDate = c.OrderDate,
                ChunkIndex = c.ChunkIndex, PageNumber = c.PageNumber, ChunkText = c.ChunkText, Embedding = c.Embedding,
                EmbeddingModel = c.EmbeddingModel, EmbeddingDimensions = c.EmbeddingDimensions, ChunkingVersion = c.ChunkingVersion,
                CreatedDate = c.CreatedDate
            });
        await db.SaveChangesAsync(ct);
        return (toEnqueue, docIdMap);
    }

    /// <summary>Deferred, §10: a content-addressed shared store would let two requests reusing the same
    /// source share one file on disk instead of a byte-for-byte copy each. A plain copy is correct today and
    /// simple; it just isn't the cheapest possible one.</summary>
    private async Task<string?> CopyRetainedFileAsync(long requestId, long newDocumentId, string sourcePath, CancellationToken ct)
    {
        if (!File.Exists(sourcePath)) return null;
        var directory = Path.Combine(env.ContentRootPath, "App_Data", "Requests", requestId.ToString(), "litigation-orders");
        Directory.CreateDirectory(directory);
        var newPath = Path.Combine(directory, $"{newDocumentId}-reused.pdf");
        await using var src = File.OpenRead(sourcePath);
        await using var dst = File.Create(newPath);
        await src.CopyToAsync(dst, ct);
        return newPath;
    }

    /// <summary>Only when the specific snapshot being reused was itself the trigger of a completed analysis —
    /// never just "the source request's latest analysis," which could belong to a different, later or
    /// earlier report of the same request (same company re-searched, or re-analysed after a rerun) and would
    /// then attach unrelated evidence to the cases actually being copied. An in-flight or never-started
    /// analysis for this snapshot is left alone; the reusing request's own subsequent analysis auto-start
    /// (plan §4.2) will find the copied snapshot's <see cref="LitigationReportSnapshot.OriginSnapshotId"/> and
    /// join whatever is (or becomes) the one Auto run for that origin, exactly as if it had bought the report
    /// itself.</summary>
    private async Task CopyAnalysisAsync(long sourceSnapshotId, long requestId, long newSnapshotId, long originId, Dictionary<long, long> caseIdMap,
        Dictionary<long, long> orderIdMap, Dictionary<long, long> docIdMap, CancellationToken ct)
    {
        var sourceRun = await db.LitigationAiAnalysisRuns.AsNoTracking()
            .Where(r => r.TriggerSnapshotId == sourceSnapshotId
                && (r.Status == LitigationAiAnalysisRunStatus.Completed || r.Status == LitigationAiAnalysisRunStatus.CompletedWithErrors))
            .OrderByDescending(r => r.LitigationAiAnalysisRunId).FirstOrDefaultAsync(ct);
        if (sourceRun is null) return;

        var run = new LitigationAiAnalysisRun
        {
            RequestId = requestId, RunNumber = 1, Trigger = LitigationAiAnalysisTrigger.Reused,
            TriggerSnapshotId = newSnapshotId, OriginSnapshotId = originId, Status = sourceRun.Status,
            ModelId = sourceRun.ModelId, PromptVersion = sourceRun.PromptVersion, AttemptCount = sourceRun.AttemptCount,
            CreatedUtc = time.GetUtcNow().UtcDateTime, StartedUtc = sourceRun.StartedUtc, CompletedUtc = sourceRun.CompletedUtc,
            FailureReason = sourceRun.FailureReason
        };
        db.LitigationAiAnalysisRuns.Add(run);
        await db.SaveChangesAsync(ct);

        // Byte-for-byte, deliberately not remapped to the new case ids: this is a provenance snapshot of
        // what the ORIGINAL analysis actually evaluated, verifiable against it — only the FK below moves to
        // the copy's own case row so request-scoped queries still find it.
        var caseAnalyses = await db.LitigationCaseAiAnalyses.AsNoTracking()
            .Where(a => a.LitigationAiAnalysisRunId == sourceRun.LitigationAiAnalysisRunId).ToListAsync(ct);
        foreach (var a in caseAnalyses)
        {
            if (!caseIdMap.TryGetValue(a.LitigationCaseId, out var newCaseId)) continue; // defensive: every analysed case was copied above
            db.LitigationCaseAiAnalyses.Add(new LitigationCaseAiAnalysis
            {
                LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, LitigationCaseId = newCaseId, Status = a.Status,
                EvidenceJson = a.EvidenceJson, EvidenceHash = a.EvidenceHash, PromptHash = a.PromptHash,
                RawResponseJson = a.RawResponseJson, ResponseHash = a.ResponseHash, AnalysisJson = a.AnalysisJson,
                FailureReason = a.FailureReason, PromptTokenCount = a.PromptTokenCount, ResponseTokenCount = a.ResponseTokenCount,
                CompletedUtc = a.CompletedUtc
            });
        }

        // Order-outcome classifications (#195) copy the same way: evidence/response verbatim, only the owning
        // request/case/order/document ids move to the copies so this request's outcome lookup finds them.
        var classifications = await db.LitigationOrderClassifications.AsNoTracking()
            .Where(c => c.LitigationAiAnalysisRunId == sourceRun.LitigationAiAnalysisRunId).ToListAsync(ct);
        foreach (var c in classifications)
        {
            if (!caseIdMap.TryGetValue(c.LitigationCaseId, out var newCaseId) || !orderIdMap.TryGetValue(c.LitigationCaseOrderId, out var newOrderId)
                || !docIdMap.TryGetValue(c.LitigationOrderDocumentId, out var newDocId)) continue; // defensive: every classified order was copied above
            db.LitigationOrderClassifications.Add(new LitigationOrderClassification
            {
                LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, RequestId = requestId, LitigationCaseId = newCaseId,
                LitigationCaseOrderId = newOrderId, LitigationOrderDocumentId = newDocId, Status = c.Status,
                OutcomeTypesJson = c.OutcomeTypesJson, FineAmount = c.FineAmount, Confidence = c.Confidence, EvidenceTruncated = c.EvidenceTruncated,
                EvidenceJson = c.EvidenceJson, EvidenceHash = c.EvidenceHash, PromptHash = c.PromptHash,
                RawResponseJson = c.RawResponseJson, ResponseHash = c.ResponseHash, ClassificationJson = c.ClassificationJson,
                FailureReason = c.FailureReason, CompletedUtc = c.CompletedUtc
            });
        }

        var portfolio = await db.LitigationPortfolioAiAnalyses.AsNoTracking()
            .FirstOrDefaultAsync(p => p.LitigationAiAnalysisRunId == sourceRun.LitigationAiAnalysisRunId, ct);
        if (portfolio is not null)
            db.LitigationPortfolioAiAnalyses.Add(new LitigationPortfolioAiAnalysis
            {
                LitigationAiAnalysisRunId = run.LitigationAiAnalysisRunId, Status = portfolio.Status,
                EvidenceJson = portfolio.EvidenceJson, EvidenceHash = portfolio.EvidenceHash, PromptHash = portfolio.PromptHash,
                RawResponseJson = portfolio.RawResponseJson, ResponseHash = portfolio.ResponseHash, AnalysisJson = portfolio.AnalysisJson,
                FailureReason = portfolio.FailureReason, PromptTokenCount = portfolio.PromptTokenCount, ResponseTokenCount = portfolio.ResponseTokenCount,
                CompletedUtc = portfolio.CompletedUtc
            });
        await db.SaveChangesAsync(ct);
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is SqlException { Number: 2627 or 2601 };
}
