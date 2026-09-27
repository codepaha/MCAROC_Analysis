using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.CompanyMaster;

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
    /// <summary>A stage whose prerequisites are met and which nobody has started — the one state an
    /// <c>Enforce</c>-mode action may act on.</summary>
    public const string ReadyToStart = "WOULD_START";

    /// <summary>Every stage that needs the company's identity waits on <c>Resolve</c> with this code while it is
    /// unresolved — so nothing that fetches, spends or searches can run for an uncertain company (plan §5.7, §5A.3).</summary>
    public const string AwaitingResolve = "AWAITING_RESOLVE";
    /// <summary>The litigation search or analysis is ready, but the identity isn't trusted for an unattended start
    /// (§5A.3 trust ladder); a reviewer can still start it by hand.</summary>
    public const string AwaitingTrustedIdentity = "AWAITING_TRUSTED_IDENTITY";

    public const string IdentityAmbiguous = ResolutionReasonCodes.Ambiguous;
    public const string IdentityNotFound = ResolutionReasonCodes.NotFound;
    public const string IdentityNeedsConfirmation = ResolutionReasonCodes.NeedsConfirmation;
    public const string IdentityNotResolved = "IDENTITY_NOT_RESOLVED";

    /// <summary>A stage the decider found genuinely stuck — a worker claimed it and stopped making progress
    /// without ever reaching its own failure path (plan §6.3). Never auto-retried
    /// (<see cref="PipelineFailureClassifier"/>); the board's manual "Restart stage" is the only way out.</summary>
    public const string StageStalled = "STAGE_STALLED";

    /// <summary>Pure except for the clock and the configured thresholds, both optional so every existing
    /// caller — none of which populates the heartbeat/lease facts §6.3 reads — is unaffected: no fact means
    /// no possible stall regardless of what <paramref name="now"/> resolves to. Production always passes both
    /// explicitly (<see cref="PipelineReconciler"/>, from its own injected clock) to stay fully deterministic.</summary>
    public static PipelineDecision Decide(PipelineSnapshot s, PipelinePolicy policy, DateTime? now = null, PipelineStallOptions? stall = null)
    {
        var clock = now ?? DateTime.UtcNow;
        var stallOpts = stall ?? new PipelineStallOptions();
        var manual = s.AutoFetch is null;
        var v = new Dictionary<PipelineStage, StageVerdict>();

        v[PipelineStage.Resolve] = Resolve(s, manual);
        // An unresolved identity holds back everything that would act on the company. Ingestion that already
        // happened stands (a manual upload identifies itself), so only a request with nothing ingested waits.
        var identityPending = !v[PipelineStage.Resolve].IsDone && v[PipelineStage.Resolve].State != PipelineStageStateKind.Skipped;
        v[PipelineStage.Unlock] = identityPending ? Waiting(AwaitingResolve) : Unlock(s, manual);
        v[PipelineStage.Refresh] = identityPending ? Waiting(AwaitingResolve) : Refresh(s, manual);
        v[PipelineStage.Fetch] = identityPending ? Waiting(AwaitingResolve) : Fetch(s, manual, v[PipelineStage.Unlock], v[PipelineStage.Refresh], clock, stallOpts);
        v[PipelineStage.Ingest] = identityPending && s.LatestCompletedIngestionRunId is null
            ? Waiting(AwaitingResolve)
            : Ingest(s, manual, v[PipelineStage.Fetch]);
        v[PipelineStage.Analysis] = Analysis(s, v[PipelineStage.Ingest], clock, stallOpts);
        v[PipelineStage.CalcAssurance] = CalcAssurance(s, v[PipelineStage.Analysis]);
        v[PipelineStage.Dossier] = Dossier(s, v[PipelineStage.Analysis], v[PipelineStage.CalcAssurance]);
        v[PipelineStage.Filings] = Filings(s, clock, stallOpts);
        // A search that already exists is still reported; nothing new starts for an unresolved company.
        v[PipelineStage.Litigation] = identityPending && s.LitigationSearch is null
            ? Waiting(AwaitingResolve)
            : Litigation(s, policy, v[PipelineStage.Ingest], clock);
        v[PipelineStage.LitigationAnalysis] = identityPending && s.LitigationAnalysis is null
            ? Waiting(AwaitingResolve)
            : LitigationAnalysis(s, policy, v[PipelineStage.Litigation], clock);

        var states = v.ToDictionary(kv => kv.Key, kv => kv.Value.State);
        var skipKinds = v.ToDictionary(kv => kv.Key, kv => kv.Value.SkipKind);
        var cancelled = s.RequestStatus == RequestStatus.Cancelled;
        return new PipelineDecision(v,
            PipelineOutcomeCalculator.Aggregate(states, skipKinds, cancelled),
            !cancelled && PipelineOutcomeCalculator.IsCoreReady(states, skipKinds));
    }

    /// <summary>Issue #295: the request's identity, from the resolutions recorded for it (#294). A false accept —
    /// the reference tool's free preview naming a different company than an auto-selected one — reopens the stage
    /// even though it had succeeded; so do an ambiguous, unmatched or unconfirmed name. Each parks as
    /// <c>NeedsAttention</c> for a human to select the company on the board's ambiguity queue.</summary>
    private static StageVerdict Resolve(PipelineSnapshot s, bool manual)
    {
        var latest = s.LatestResolution;
        if (latest is { IsFalseAccept: true })
            return Attention(ResolutionReasonCodes.FalseAccept,
                "The reference tool's preview named a different company than the auto-selected one; nothing was fetched or spent. Select the correct company.",
                latest.ResolutionId);

        if (!string.IsNullOrWhiteSpace(s.Cin) || !string.IsNullOrWhiteSpace(s.Llpin))
        {
            // A later selection of a different company was refused (REQUEST_ALREADY_IDENTIFIED, or a collision):
            // the existing identifier is now disputed, so it must not carry the pipeline on until a person
            // confirms which company the request is for.
            if (latest is { Status: ResolutionStatus.Resolved, AppliedToRequest: false, ChosenIdentifier: { } rejected }
                && !string.Equals(rejected, s.Cin, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(rejected, s.Llpin, StringComparison.OrdinalIgnoreCase))
                return Attention(latest.ReasonCode,
                    $"{rejected} was selected, but the request already names {s.Cin ?? s.Llpin}; nothing was changed. " +
                    "Confirm which company this request is for.", latest.ResolutionId);
            return s.AppliedResolution is { } applied ? Ok(applied.ResolutionId, applied.ReasonCode) : Ok();
        }

        if (latest is not null)
            return latest.Status switch
            {
                ResolutionStatus.Ambiguous => Attention(IdentityAmbiguous,
                    "Several companies match the name and nothing distinguishes them. Select the company.", latest.ResolutionId),
                ResolutionStatus.NotFound or ResolutionStatus.InvalidInput => Attention(IdentityNotFound,
                    latest.ReasonCode == IdentityNotFound
                        ? "No company in the master data matches the name. Select the company or correct the request."
                        : $"{latest.ReasonCode}: the company could not be identified. Select the company or correct the request.",
                    latest.ResolutionId),
                ResolutionStatus.NeedsConfirmation => Attention(IdentityNeedsConfirmation,
                    $"One company stands out{(latest.RecommendedIdentifier is { } r ? $" ({r})" : "")}, but a person must confirm it.",
                    latest.ResolutionId),
                // Resolved but not written to the request: a collision with the client's existing request, or a
                // request that already names a different company.
                _ => Attention(latest.ReasonCode,
                    latest.ExistingRequestId is { } other
                        ? $"This client already has request {other} for {latest.ChosenIdentifier}."
                        : $"{latest.ChosenIdentifier} was selected but not applied to the request.",
                    latest.ResolutionId)
            };

        // A manual upload is identified by the workbook itself.
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        return Attention(IdentityNotResolved, "The request has no CIN/LLPIN.");
    }

    private static StageVerdict Unlock(PipelineSnapshot s, bool manual)
    {
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        var job = s.AutoFetch!;
        // Data already exported for this request stands even if the unlock has lapsed since.
        if (job.RocDocumentId is not null)
            return s.Lifecycle?.UnlockedUtc is not null
                ? Ok(job.JobId, "UNLOCKED")
                : Ok(job.JobId, "INFERRED_FROM_EXPORT", "The workbook export succeeded, which requires an unlocked company.");
        return s.Lifecycle switch
        {
            { State: CompanyReportLifecycleState.Locked } => Attention("UNLOCK_APPROVAL_REQUIRED", "The company is locked in the reference tool; unlocking costs 1 credit and needs an approval."),
            { State: CompanyReportLifecycleState.Expired } => Attention("UNLOCK_EXPIRED", "The company's 12-month unlock in the reference tool has expired."),
            { UnlockedUtc: not null } => Ok(job.JobId, "UNLOCKED"),
            _ => new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: "NOT_YET_CHECKED",
                ReasonDetail: "Checked by auto-fetch before its first export.")
        };
    }

    private static StageVerdict Refresh(PipelineSnapshot s, bool manual)
    {
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        var job = s.AutoFetch!;
        var l = s.Lifecycle;
        if (!s.RefreshGateEnabled || (job.RocDocumentId is not null && l is null))
            return Skip(PipelineStageSkipKind.Neutral, "REFRESH_NOT_TRACKED",
                "This export ran without the refresh gate (ReferenceTool:RefreshBeforeFetch off, or before it existed).");
        // The export only runs once the gate confirmed data under 24 hours old.
        if (job.RocDocumentId is not null) return Ok(job.JobId, "DATA_CURRENT");
        if (l is null) return Waiting("AWAITING_REFRESH_CHECK", job.JobId);
        if (l.State == CompanyReportLifecycleState.RefreshFailed)
            return Attention("REFRESH_TIMEOUT", "The reference tool did not finish refreshing in time; retry to request a new refresh.", job.JobId);
        if (l.RefreshActive) return Running(job.JobId, "REFRESHING");
        if (l.State is CompanyReportLifecycleState.Locked or CompanyReportLifecycleState.Expired) return Waiting("AWAITING_UNLOCK", job.JobId);
        if (job.Status == AutoFetchJobStatus.Failed) return Waiting("AWAITING_RETRY", job.JobId);
        return Running(job.JobId, "CHECKING");
    }

    private static StageVerdict Fetch(PipelineSnapshot s, bool manual, StageVerdict unlock, StageVerdict refresh, DateTime now, PipelineStallOptions stall)
    {
        if (manual) return Skip(PipelineStageSkipKind.Neutral, "MANUAL_SOURCE");
        var job = s.AutoFetch!;
        if (job.RocDocumentId is not null) return Ok(job.JobId);
        if (job.Status == AutoFetchJobStatus.Failed)
            // A locked company or a timed-out refresh is already reported on its own stage.
            return unlock.State == PipelineStageStateKind.NeedsAttention || refresh.State == PipelineStageStateKind.NeedsAttention
                ? Waiting("BLOCKED_UPSTREAM", job.JobId)
                : Attention("FETCH_FAILED", job.FailureReason, job.JobId);
        if (job.Status == AutoFetchJobStatus.Queued) return Waiting("QUEUED", job.JobId);
        if (job.Status == AutoFetchJobStatus.WaitingForRefresh) return Waiting("AWAITING_REFRESH", job.JobId);
        if (job.Status == AutoFetchJobStatus.WaitingForUnlock) return Waiting("AWAITING_UNLOCK", job.JobId);
        if (IsStalled(job.HeartbeatUtc, now, stall.FetchMinutes))
            return Attention(StageStalled, $"No download progress for over {stall.FetchMinutes} minute(s); the worker may have crashed. Restart the stage.", job.JobId);
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

    private static StageVerdict Analysis(PipelineSnapshot s, StageVerdict ingest, DateTime now, PipelineStallOptions stall)
    {
        if (!ingest.IsDone) return Waiting("AWAITING_INGEST");
        if (s.Analysis is { } a)
            return a.Status switch
            {
                AnalysisRunStatus.Completed => Ok(a.Id),
                AnalysisRunStatus.CompletedWithErrors => Warn("ANALYSIS_COMPLETED_WITH_ERRORS", a.FailureReason, a.Id),
                AnalysisRunStatus.Failed => Attention("ANALYSIS_FAILED", a.FailureReason, a.Id),
                _ => IsStalled(a.StartedUtc, now, stall.AnalysisMinutes)
                    ? Attention(StageStalled, $"Still running after over {stall.AnalysisMinutes} minute(s), longer than a normal pass takes. Restart the stage.", a.Id)
                    : Running(a.Id)
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

    private static StageVerdict Filings(PipelineSnapshot s, DateTime now, PipelineStallOptions stall)
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
        // Once the main export is done the job keeps running to fetch filing documents — the same worker,
        // the same heartbeat, just a phase Fetch itself no longer reports on (it already returned Ok once
        // RocDocumentId was set). Before that point this is a genuine wait on Fetch, whose own stall check
        // already covers it, so only check staleness once there's a filing-download phase to actually stall in.
        if (job.RocDocumentId is not null && IsStalled(job.HeartbeatUtc, now, stall.FetchMinutes))
            return Attention(StageStalled, $"No progress downloading filing documents for over {stall.FetchMinutes} minute(s); the worker may have crashed. Restart the stage.", job.JobId);
        return Waiting("AWAITING_FETCH");
    }

    private static StageVerdict Litigation(PipelineSnapshot s, PipelinePolicy policy, StageVerdict ingest, DateTime now)
    {
        if (s.LitigationSearch is { } job)
        {
            return job.Status switch
            {
                LitigationSearchJobStatus.Failed => Attention("LITIGATION_SEARCH_FAILED", job.FailureReason, job.JobId),
                LitigationSearchJobStatus.Completed => job.SnapshotStatus switch
                {
                    LitigationReportSnapshotStatus.Completed => job.HasStalledOrderDownload
                        ? Attention("ORDER_DOWNLOAD_STALLED", "An order document has been waiting to download for 5 or more days.", job.SnapshotId)
                        : Ok(job.SnapshotId),
                    LitigationReportSnapshotStatus.Failed => Attention("LITIGATION_IMPORT_FAILED", null, job.SnapshotId),
                    // The import is a separate crash-safe unit of work from the search job itself, claimed
                    // with its own lease on LitigationReportSnapshot — the job's own lease (checked below)
                    // says nothing about whether the import that started after it finished is still alive.
                    _ => IsLeaseExpired(job.SnapshotLeaseExpiresUtc, now)
                        ? Attention(StageStalled, "The import's lease expired without finishing; its worker may have crashed. Restart the stage.", job.SnapshotId)
                        : Running(job.JobId, "IMPORTING")
                },
                _ => IsLeaseExpired(job.LeaseExpiresUtc, now)
                    ? Attention(StageStalled, "The search's lease expired without finishing; its worker may have crashed. Restart the stage.", job.JobId)
                    : Running(job.JobId)
            };
        }
        if (!policy.LitigationSearch) return Skip(PipelineStageSkipKind.Neutral, "POLICY_OFF");
        if (!s.LitigationConfigured) return Skip(PipelineStageSkipKind.Warning, "INTEGRATION_NOT_CONFIGURED");
        if (!ingest.IsDone) return Waiting("AWAITING_INGEST");
        var trust = IdentityTrust.Evaluate(s.AppliedResolution, s.SpendThreshold);
        if (!trust.Trusted)
            return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: AwaitingTrustedIdentity,
                ReasonDetail: $"{trust.Detail} A reviewer can start the search by hand.");
        return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: ReadyToStart,
            ReasonDetail: "Ready to start. The coordinator starts it itself only in Enforce mode with Enforce:Litigation on.");
    }

    private static StageVerdict LitigationAnalysis(PipelineSnapshot s, PipelinePolicy policy, StageVerdict litigation, DateTime now)
    {
        if (s.LitigationAnalysis is { } run)
        {
            return run.Status switch
            {
                LitigationAiAnalysisRunStatus.Completed => Ok(run.Id),
                LitigationAiAnalysisRunStatus.CompletedWithErrors => Warn("LITIGATION_ANALYSIS_COMPLETED_WITH_ERRORS", run.FailureReason, run.Id),
                LitigationAiAnalysisRunStatus.Failed => Attention("LITIGATION_ANALYSIS_FAILED", run.FailureReason, run.Id),
                _ => IsLeaseExpired(run.LeaseExpiresUtc, now)
                    ? Attention(StageStalled, "The analysis's lease expired without finishing; its worker may have crashed. Restart the stage.", run.Id)
                    : Running(run.Id)
            };
        }
        if (!policy.LitigationAnalysis) return Skip(PipelineStageSkipKind.Neutral, "POLICY_OFF");
        // Any warning about the search itself is already counted on the Litigation stage.
        if (litigation.State == PipelineStageStateKind.Skipped) return Skip(PipelineStageSkipKind.Neutral, "UPSTREAM_SKIPPED");
        if (!litigation.IsDone) return Waiting("AWAITING_LITIGATION");
        // §4.2: every order document must be terminal for download and (where it has text) for chunking
        // before analysis reads the evidence — an in-flight download/chunk would otherwise be silently
        // missing from the prompt the AI actually sees.
        if (s.LitigationSearch?.OrdersFullyProcessed != true) return Waiting("AWAITING_ORDER_PROCESSING");
        // The analysis is a real (Vertex AI) spend: same trust ladder as the automatic search (§5A.3).
        var trust = IdentityTrust.Evaluate(s.AppliedResolution, s.SpendThreshold);
        if (!trust.Trusted)
            return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: AwaitingTrustedIdentity,
                ReasonDetail: $"{trust.Detail} A reviewer can start the analysis by hand.");
        return new StageVerdict(PipelineStageStateKind.NotStarted, ReasonCode: ReadyToStart,
            ReasonDetail: "Ready to start. The coordinator starts it itself only in Enforce mode with Enforce:LitigationAnalysis on.");
    }

    /// <summary>No heartbeat recorded means no stall signal exists yet (a fresh claim, or an older row from
    /// before this field existed) — never treated as stalled, since there is nothing to measure against.</summary>
    private static bool IsStalled(DateTime? heartbeatUtc, DateTime now, int thresholdMinutes) =>
        heartbeatUtc is { } h && now - h > TimeSpan.FromMinutes(thresholdMinutes);

    /// <summary>No lease recorded (not yet claimed, or a row from before leases existed) is never a stall —
    /// same reasoning as <see cref="IsStalled"/>.</summary>
    private static bool IsLeaseExpired(DateTime? leaseExpiresUtc, DateTime now) => leaseExpiresUtc is { } exp && now > exp;

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
