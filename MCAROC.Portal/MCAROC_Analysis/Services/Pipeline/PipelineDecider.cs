using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Pipeline;

public sealed record StageVerdict(
    PipelineStageStateKind State,
    PipelineStageSkipKind? SkipKind = null,
    string? ReasonCode = null,
    string? ReasonDetail = null,
    long? SourceRef = null)
{
    /// <summary>Done for dependency purposes: succeeded (with or without warnings) or a neutral skip.</summary>
    public bool IsDone => State is PipelineStageStateKind.Succeeded or PipelineStageStateKind.SucceededWithWarnings
        || (State == PipelineStageStateKind.Skipped && SkipKind == PipelineStageSkipKind.Neutral);
}

public sealed record PipelineDecision(IReadOnlyDictionary<PipelineStage, StageVerdict> Stages, PipelineOutcome Outcome, bool CoreReady);

/// <summary>Pure: facts + policy → every stage's state and the request outcome (docs/pipeline-automation-plan.md
/// §3.3/§3.4). Observe mode only — it decides what each stage *is*, never an action to take, so unlike the
/// plan's eventual signature it needs neither the previous states nor the clock.</summary>
public static class PipelineDecider
{
    public static PipelineDecision Decide(PipelineSnapshot s, PipelinePolicy policy)
    {
        var manual = s.AutoFetch is null;
        var v = new Dictionary<PipelineStage, StageVerdict>();

        v[PipelineStage.Resolve] = Resolve(s, manual);
        v[PipelineStage.Unlock] = Unlock(s, manual);
        v[PipelineStage.Refresh] = manual
            ? Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE")
            : Skip(PipelineStageSkipKind.Neutral, "REFRESH_NOT_TRACKED",
                "Auto-fetch exports the reference tool's current data; the refresh lifecycle is tracked from #229.");
        v[PipelineStage.Fetch] = Fetch(s, manual);
        v[PipelineStage.Ingest] = Ingest(s, manual, v[PipelineStage.Fetch]);
        v[PipelineStage.Analysis] = Analysis(s, v[PipelineStage.Ingest]);
        v[PipelineStage.CalcAssurance] = CalcAssurance(s, v[PipelineStage.Analysis]);
        v[PipelineStage.Dossier] = Dossier(s, v[PipelineStage.Analysis], v[PipelineStage.CalcAssurance]);
        v[PipelineStage.Filings] = Filings(s);
        v[PipelineStage.Litigation] = Litigation(s, policy, v[PipelineStage.Ingest]);
        v[PipelineStage.LitigationAnalysis] = LitigationAnalysis(s, policy, v[PipelineStage.Litigation]);

        var states = v.ToDictionary(kv => kv.Key, kv => kv.Value.State);
        var skipKinds = v.ToDictionary(kv => kv.Key, kv => kv.Value.SkipKind);
        var cancelled = s.RequestStatus == RequestStatus.Cancelled;
        return new PipelineDecision(v,
            PipelineOutcomeCalculator.Aggregate(states, skipKinds, cancelled),
            !cancelled && PipelineOutcomeCalculator.IsCoreReady(states, skipKinds));
    }

    private static StageVerdict Resolve(PipelineSnapshot s, bool manual)
    {
        if (!string.IsNullOrWhiteSpace(s.Cin) || !string.IsNullOrWhiteSpace(s.Llpin)) return Ok();
        // A manual upload is identified by the workbook itself.
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        return Attention("IDENTITY_NOT_RESOLVED", "The request has no CIN/LLPIN.");
    }

    private static StageVerdict Unlock(PipelineSnapshot s, bool manual)
    {
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        // Until #266 records unlocks, the only evidence is indirect: the tool only exports a workbook for an
        // unlocked company.
        if (s.AutoFetch!.RocDocumentId is not null)
            return Ok(s.AutoFetch.JobId, "INFERRED_FROM_EXPORT", "The workbook export succeeded, which requires an unlocked company.");
        return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: "NOT_YET_TRACKED",
            ReasonDetail: "Unlock state is tracked from #266; until then it is inferred from a successful workbook export.");
    }

    private static StageVerdict Fetch(PipelineSnapshot s, bool manual)
    {
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        var job = s.AutoFetch!;
        if (job.RocDocumentId is not null) return Ok(job.JobId);
        if (job.Status == AutoFetchJobStatus.Failed) return Attention("FETCH_FAILED", job.FailureReason, job.JobId);
        if (job.Status == AutoFetchJobStatus.Queued) return Waiting("QUEUED", job.JobId);
        return Running(job.JobId);
    }

    private static StageVerdict Ingest(PipelineSnapshot s, bool manual, StageVerdict fetch)
    {
        if (s.LatestCompletedIngestionRunId is { } run)
            return s.HasIngestionWarnings ? Warn("INGESTION_WARNINGS", null, run) : Ok(run);
        if (!manual && !fetch.IsDone) return Waiting("AWAITING_FETCH");
        if (s.IngestionRunning) return Running();
        if (s.LatestIngestionStatus == IngestionRunStatus.Failed
            || s.RequestStatus is RequestStatus.ExtractionFailed or RequestStatus.ValidationFailed)
            return Attention("INGEST_FAILED", s.RequestFailureReason);
        return Waiting("AWAITING_INGESTION");
    }

    private static StageVerdict Analysis(PipelineSnapshot s, StageVerdict ingest)
    {
        if (!ingest.IsDone) return Waiting("AWAITING_INGEST");
        if (s.Analysis is { } a)
            return a.Status switch
            {
                AnalysisRunStatus.Completed => Ok(a.Id),
                AnalysisRunStatus.CompletedWithErrors => Warn("ANALYSIS_COMPLETED_WITH_ERRORS", a.FailureReason, a.Id),
                AnalysisRunStatus.Failed => Attention("ANALYSIS_FAILED", a.FailureReason, a.Id),
                _ => Running(a.Id)
            };
        // Analysis is only auto-enqueued when ingestion didn't flag the request for manual review.
        if (s.IsManualReviewRequired) return Attention("MANUAL_REVIEW_REQUIRED", s.ManualReviewReason);
        return Waiting("AWAITING_ANALYSIS");
    }

    private static StageVerdict CalcAssurance(PipelineSnapshot s, StageVerdict analysis)
    {
        if (!analysis.IsDone) return Waiting("AWAITING_ANALYSIS");
        if (s.CalcAssuranceMode == CalculationAssuranceMode.Off) return Skip(PipelineStageSkipKind.Neutral, "CALC_ASSURANCE_OFF");
        if (s.CalcAuditSnapshotId is not { } snapshot) return Waiting("AWAITING_CHECKS");
        return s.CalcAiAuditStatus switch
        {
            null or CalculationAiAuditRunStatus.Completed => Ok(snapshot),
            CalculationAiAuditRunStatus.Pending or CalculationAiAuditRunStatus.InProgress => Running(snapshot, "AI_AUDIT_RUNNING"),
            var other => Warn("AI_AUDIT_INCOMPLETE", $"AI audit ended {other}; deterministic checks are persisted.", snapshot)
        };
    }

    private static StageVerdict Dossier(PipelineSnapshot s, StageVerdict analysis, StageVerdict calc)
    {
        if (!analysis.IsDone || !calc.IsDone) return Waiting("AWAITING_CALC_ASSURANCE");
        if (s.DossierHoldActive && s.CalcAssuranceMode == CalculationAssuranceMode.Enforced)
            return Attention("CALC_GATE_HOLD", "A calculation-assurance hold blocks the dossier download.", s.Analysis?.Id);
        if (s.DossierHoldActive && s.CalcAssuranceMode == CalculationAssuranceMode.ObserveOnly)
            return Warn("CALC_GATE_WOULD_HOLD", "Downloadable, but Enforced mode would hold it.", s.Analysis?.Id);
        return Ok(s.Analysis?.Id);
    }

    private static StageVerdict Filings(PipelineSnapshot s)
    {
        var job = s.AutoFetch;
        if (s.Filings is { } f)
        {
            return f.Status switch
            {
                FilingBatchStatus.Failed => Attention("FILINGS_FAILED", f.FailureReason, f.BatchId),
                FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors when f.OutstandingChunks > 0 => Running(f.BatchId, "CHUNKING"),
                FilingBatchStatus.Completed or FilingBatchStatus.CompletedWithErrors when f.FailedChunks > 0 || f.Status == FilingBatchStatus.CompletedWithErrors =>
                    Warn("FILINGS_COMPLETED_WITH_ERRORS", $"{f.FailedChunks} document(s) failed chunking.", f.BatchId),
                FilingBatchStatus.Completed => Ok(f.BatchId),
                _ => Running(f.BatchId)
            };
        }
        if (job is null || !job.IncludeFilings) return Skip(PipelineStageSkipKind.Neutral, "NO_FILINGS_REQUESTED");
        if (job.Status == AutoFetchJobStatus.Failed)
            return job.RocDocumentId is null ? Waiting("AWAITING_FETCH") : Attention("FILINGS_FETCH_FAILED", job.FailureReason, job.JobId);
        if (job.IsTerminal) return Skip(PipelineStageSkipKind.Warning, "NO_FILINGS_AVAILABLE", "The reference tool listed no filings to download.");
        return Waiting("AWAITING_FETCH");
    }

    private static StageVerdict Litigation(PipelineSnapshot s, PipelinePolicy policy, StageVerdict ingest)
    {
        if (s.LitigationSearch is { } job)
        {
            return job.Status switch
            {
                LitigationSearchJobStatus.Failed => Attention("LITIGATION_SEARCH_FAILED", job.FailureReason, job.JobId),
                LitigationSearchJobStatus.Completed => job.SnapshotStatus switch
                {
                    LitigationReportSnapshotStatus.Completed => Ok(job.SnapshotId),
                    LitigationReportSnapshotStatus.Failed => Attention("LITIGATION_IMPORT_FAILED", null, job.SnapshotId),
                    _ => Running(job.JobId, "IMPORTING")
                },
                _ => Running(job.JobId)
            };
        }
        if (!policy.LitigationSearch) return Skip(PipelineStageSkipKind.Neutral, "POLICY_OFF");
        if (!s.LitigationConfigured) return Skip(PipelineStageSkipKind.Warning, "INTEGRATION_NOT_CONFIGURED");
        if (!ingest.IsDone) return Waiting("AWAITING_INGEST");
        return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: "WOULD_START",
            ReasonDetail: "Observe mode: the coordinator does not start litigation searches yet.");
    }

    private static StageVerdict LitigationAnalysis(PipelineSnapshot s, PipelinePolicy policy, StageVerdict litigation)
    {
        if (s.LitigationAnalysis is { } run)
        {
            return run.Status switch
            {
                LitigationAiAnalysisRunStatus.Completed => Ok(run.Id),
                LitigationAiAnalysisRunStatus.CompletedWithErrors => Warn("LITIGATION_ANALYSIS_COMPLETED_WITH_ERRORS", run.FailureReason, run.Id),
                LitigationAiAnalysisRunStatus.Failed => Attention("LITIGATION_ANALYSIS_FAILED", run.FailureReason, run.Id),
                _ => Running(run.Id)
            };
        }
        if (!policy.LitigationAnalysis) return Skip(PipelineStageSkipKind.Neutral, "POLICY_OFF");
        // Any warning about the search itself is already counted on the Litigation stage.
        if (litigation.State == PipelineStageStateKind.Skipped) return Skip(PipelineStageSkipKind.Neutral, "UPSTREAM_SKIPPED");
        if (!litigation.IsDone) return Waiting("AWAITING_LITIGATION");
        return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: "WOULD_START",
            ReasonDetail: "Observe mode: the coordinator does not start litigation analysis yet.");
    }

    private static StageVerdict Ok(long? sourceRef = null, string? code = null, string? detail = null) =>
        new(PipelineStageStateKind.Succeeded, null, code, detail, sourceRef);
    private static StageVerdict Warn(string code, string? detail, long? sourceRef) =>
        new(PipelineStageStateKind.SucceededWithWarnings, null, code, detail, sourceRef);
    private static StageVerdict Running(long? sourceRef = null, string? code = null) =>
        new(PipelineStageStateKind.Running, null, code, null, sourceRef);
    private static StageVerdict Waiting(string code, long? sourceRef = null) =>
        new(PipelineStageStateKind.Waiting, null, code, null, sourceRef);
    private static StageVerdict Attention(string code, string? detail, long? sourceRef = null) =>
        new(PipelineStageStateKind.NeedsAttention, null, code, detail, sourceRef);
    private static StageVerdict Skip(PipelineStageSkipKind kind, string code, string? detail = null) =>
        new(PipelineStageStateKind.Skipped, kind, code, detail);
}
