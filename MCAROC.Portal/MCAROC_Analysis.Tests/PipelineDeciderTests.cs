using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

/// <summary>Table tests for every dependency edge the decider encodes (docs/pipeline-automation-plan.md §3.3),
/// plus a generated test that the edges hold for arbitrary fact combinations.</summary>
public sealed class PipelineDeciderTests
{
    private static readonly PipelinePolicy PolicyOff = new();

    /// <summary>A manual-upload request whose core track has fully succeeded.</summary>
    private static PipelineSnapshot ManualDone() => new()
    {
        RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.AnalysisCompleted,
        LatestCompletedIngestionRunId = 10, LatestIngestionStatus = IngestionRunStatus.CompletedClean,
        Analysis = new(20, AnalysisRunStatus.Completed, null), CalcAssuranceMode = CalculationAssuranceMode.Off
    };

    private static PipelineSnapshot AutoDone() => ManualDone() with
    {
        AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Completed, RocDocumentId: 7, IncludeFilings: true, FilingBatchId: 9, null),
        Filings = new FilingFacts(9, FilingBatchStatus.Completed, null, OutstandingChunks: 0, FailedChunks: 0)
    };

    private static PipelineStageStateKind State(PipelineDecision d, PipelineStage s) => d.Stages[s].State;

    [Fact]
    public void Manual_upload_with_everything_done_is_complete_and_skips_the_reference_tool_stages_neutrally()
    {
        var d = PipelineDecider.Decide(ManualDone(), PolicyOff);

        Assert.Equal(PipelineOutcome.Complete, d.Outcome);
        Assert.True(d.CoreReady);
        foreach (var stage in new[] { PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch })
        {
            Assert.Equal(PipelineStageStateKind.Skipped, State(d, stage));
            Assert.Equal(PipelineStageSkipKind.Neutral, d.Stages[stage].SkipKind);
            Assert.Equal("MANUAL_SOURCE", d.Stages[stage].ReasonCode);
        }
        Assert.Equal(PipelineStageStateKind.Succeeded, State(d, PipelineStage.Resolve));
        Assert.Equal("NO_FILINGS_REQUESTED", d.Stages[PipelineStage.Filings].ReasonCode);
        Assert.Equal("POLICY_OFF", d.Stages[PipelineStage.Litigation].ReasonCode);
    }

    [Fact]
    public void Auto_fetch_with_everything_done_is_complete_with_unlock_inferred_and_refresh_marked_untracked()
    {
        var d = PipelineDecider.Decide(AutoDone(), PolicyOff);

        Assert.Equal(PipelineOutcome.Complete, d.Outcome);
        Assert.Equal(PipelineStageStateKind.Succeeded, State(d, PipelineStage.Unlock));
        Assert.Equal("INFERRED_FROM_EXPORT", d.Stages[PipelineStage.Unlock].ReasonCode);
        Assert.Equal("REFRESH_NOT_TRACKED", d.Stages[PipelineStage.Refresh].ReasonCode);
        Assert.Equal(PipelineStageStateKind.Succeeded, State(d, PipelineStage.Fetch));
        Assert.Equal(5, d.Stages[PipelineStage.Fetch].SourceRef); // the job that proves the fetch
        Assert.Equal(PipelineStageStateKind.Succeeded, State(d, PipelineStage.Filings));
    }

    [Fact]
    public void Queued_auto_fetch_waits_and_everything_downstream_waits_on_it()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Queued, null, true, null, null)
        };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineOutcome.InProgress, d.Outcome);
        Assert.Equal(PipelineStageStateKind.Waiting, State(d, PipelineStage.Fetch));
        Assert.Equal("AWAITING_FETCH", d.Stages[PipelineStage.Ingest].ReasonCode);
        Assert.Equal(PipelineStageStateKind.Waiting, State(d, PipelineStage.Analysis));
        Assert.Equal(PipelineStageStateKind.Waiting, State(d, PipelineStage.Dossier));
        Assert.Equal(PipelineStageStateKind.Waiting, State(d, PipelineStage.Filings));
    }

    [Fact]
    public void Failed_fetch_raises_attention_once_not_again_on_ingest()
    {
        // The job service also marks the request ExtractionFailed when the fetch fails before ingestion.
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.ExtractionFailed,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Failed, null, true, null, "session expired")
        };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
        Assert.Equal("FETCH_FAILED", d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Equal("session expired", d.Stages[PipelineStage.Fetch].ReasonDetail);
        Assert.Single(d.Stages.Values, v => v.State == PipelineStageStateKind.NeedsAttention);
    }

    [Fact]
    public void Failed_filings_after_the_workbook_was_fetched_needs_attention_but_the_dossier_stays_ready()
    {
        var s = AutoDone() with
        {
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.Failed, 7, true, null, "no PDFs downloaded"),
            Filings = null
        };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
        Assert.True(d.CoreReady);
        Assert.Equal("FILINGS_FETCH_FAILED", d.Stages[PipelineStage.Filings].ReasonCode);
    }

    private static PipelineSnapshot AutoBeforeExport(AutoFetchJobStatus status, LifecycleFacts? lifecycle) => new()
    {
        RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created, RefreshGateEnabled = true,
        AutoFetch = new AutoFetchFacts(5, status, null, true, null, status == AutoFetchJobStatus.Failed ? "gate said no" : null),
        Lifecycle = lifecycle
    };

    [Fact]
    public void A_locked_company_raises_attention_on_unlock_only()
    {
        var d = PipelineDecider.Decide(AutoBeforeExport(AutoFetchJobStatus.Failed,
            new LifecycleFacts(CompanyReportLifecycleState.Locked, null, false)), PolicyOff);

        Assert.Equal("UNLOCK_APPROVAL_REQUIRED", d.Stages[PipelineStage.Unlock].ReasonCode);
        Assert.Equal("AWAITING_UNLOCK", d.Stages[PipelineStage.Refresh].ReasonCode);
        Assert.Equal("BLOCKED_UPSTREAM", d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Single(d.Stages.Values, v => v.State == PipelineStageStateKind.NeedsAttention);
    }

    [Fact]
    public void A_job_parked_for_unlock_approval_waits_on_fetch_with_attention_on_unlock()
    {
        var d = PipelineDecider.Decide(AutoBeforeExport(AutoFetchJobStatus.WaitingForUnlock,
            new LifecycleFacts(CompanyReportLifecycleState.Locked, null, false)), PolicyOff);

        Assert.Equal("UNLOCK_APPROVAL_REQUIRED", d.Stages[PipelineStage.Unlock].ReasonCode);
        Assert.Equal("AWAITING_UNLOCK", d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
    }

    [Fact]
    public void An_expired_unlock_raises_attention_on_unlock()
    {
        var d = PipelineDecider.Decide(AutoBeforeExport(AutoFetchJobStatus.Failed,
            new LifecycleFacts(CompanyReportLifecycleState.Expired, DateTime.UtcNow.AddMonths(-13), false)), PolicyOff);
        Assert.Equal("UNLOCK_EXPIRED", d.Stages[PipelineStage.Unlock].ReasonCode);
    }

    [Fact]
    public void A_refresh_in_flight_shows_as_running_and_the_parked_fetch_waits_on_it()
    {
        var d = PipelineDecider.Decide(AutoBeforeExport(AutoFetchJobStatus.WaitingForRefresh,
            new LifecycleFacts(CompanyReportLifecycleState.Refreshing, DateTime.UtcNow.AddMonths(-2), true)), PolicyOff);

        Assert.Equal(PipelineStageStateKind.Succeeded, State(d, PipelineStage.Unlock));
        Assert.Equal(PipelineStageStateKind.Running, State(d, PipelineStage.Refresh));
        Assert.Equal("REFRESHING", d.Stages[PipelineStage.Refresh].ReasonCode);
        Assert.Equal("AWAITING_REFRESH", d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Equal(PipelineOutcome.InProgress, d.Outcome);
    }

    [Fact]
    public void A_timed_out_refresh_needs_attention_on_refresh_not_fetch()
    {
        var d = PipelineDecider.Decide(AutoBeforeExport(AutoFetchJobStatus.Failed,
            new LifecycleFacts(CompanyReportLifecycleState.RefreshFailed, DateTime.UtcNow.AddMonths(-2), false)), PolicyOff);

        Assert.Equal("REFRESH_TIMEOUT", d.Stages[PipelineStage.Refresh].ReasonCode);
        Assert.Equal("BLOCKED_UPSTREAM", d.Stages[PipelineStage.Fetch].ReasonCode);
        Assert.Single(d.Stages.Values, v => v.State == PipelineStageStateKind.NeedsAttention);
    }

    [Fact]
    public void An_export_that_passed_the_gate_marks_unlock_and_refresh_as_real_successes()
    {
        var d = PipelineDecider.Decide(AutoDone() with
        {
            RefreshGateEnabled = true,
            Lifecycle = new LifecycleFacts(CompanyReportLifecycleState.Unlocked, DateTime.UtcNow.AddMonths(-2), false)
        }, PolicyOff);

        Assert.Equal("UNLOCKED", d.Stages[PipelineStage.Unlock].ReasonCode);
        Assert.Equal("DATA_CURRENT", d.Stages[PipelineStage.Refresh].ReasonCode);
        Assert.Equal(PipelineOutcome.Complete, d.Outcome);
    }

    [Fact]
    public void Ingestion_flagged_for_manual_review_never_gets_analysis_so_it_needs_attention_instead_of_waiting()
    {
        var s = ManualDone() with { Analysis = null, IsManualReviewRequired = true, ManualReviewReason = "CIN mismatch" };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal("MANUAL_REVIEW_REQUIRED", d.Stages[PipelineStage.Analysis].ReasonCode);
        Assert.Equal(PipelineOutcome.NeedsAttention, d.Outcome);
    }

    [Fact]
    public void Ingestion_warnings_make_the_run_complete_with_warnings()
    {
        var d = PipelineDecider.Decide(ManualDone() with { HasIngestionWarnings = true }, PolicyOff);
        Assert.Equal(PipelineStageStateKind.SucceededWithWarnings, State(d, PipelineStage.Ingest));
        Assert.Equal(PipelineOutcome.CompleteWithWarnings, d.Outcome);
    }

    [Theory]
    [InlineData(CalculationAssuranceMode.Enforced, PipelineStageStateKind.NeedsAttention, "CALC_GATE_HOLD", PipelineOutcome.NeedsAttention)]
    [InlineData(CalculationAssuranceMode.ObserveOnly, PipelineStageStateKind.SucceededWithWarnings, "CALC_GATE_WOULD_HOLD", PipelineOutcome.CompleteWithWarnings)]
    public void An_active_dossier_hold_follows_the_gate_mode(CalculationAssuranceMode mode, PipelineStageStateKind expected, string code, PipelineOutcome outcome)
    {
        var s = ManualDone() with { CalcAssuranceMode = mode, CalcAuditSnapshotId = 30, DossierHoldActive = true };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(expected, State(d, PipelineStage.Dossier));
        Assert.Equal(code, d.Stages[PipelineStage.Dossier].ReasonCode);
        Assert.Equal(outcome, d.Outcome);
    }

    [Fact]
    public void Calc_assurance_waits_for_its_checks_and_then_for_a_running_ai_audit()
    {
        var waiting = PipelineDecider.Decide(ManualDone() with { CalcAssuranceMode = CalculationAssuranceMode.Enforced }, PolicyOff);
        Assert.Equal("AWAITING_CHECKS", waiting.Stages[PipelineStage.CalcAssurance].ReasonCode);
        Assert.Equal(PipelineStageStateKind.Waiting, State(waiting, PipelineStage.Dossier));

        var running = PipelineDecider.Decide(ManualDone() with
        {
            CalcAssuranceMode = CalculationAssuranceMode.Enforced, CalcAuditSnapshotId = 30,
            CalcAiAuditStatus = CalculationAiAuditRunStatus.InProgress
        }, PolicyOff);
        Assert.Equal(PipelineStageStateKind.Running, State(running, PipelineStage.CalcAssurance));
        Assert.Equal(PipelineOutcome.InProgress, running.Outcome);

        var aiFailed = PipelineDecider.Decide(ManualDone() with
        {
            CalcAssuranceMode = CalculationAssuranceMode.Enforced, CalcAuditSnapshotId = 30,
            CalcAiAuditStatus = CalculationAiAuditRunStatus.Failed
        }, PolicyOff);
        Assert.Equal(PipelineStageStateKind.SucceededWithWarnings, State(aiFailed, PipelineStage.CalcAssurance));
        Assert.Equal(PipelineStageStateKind.Succeeded, State(aiFailed, PipelineStage.Dossier));
    }

    [Fact]
    public void Outstanding_chunking_with_core_done_is_core_ready_not_complete()
    {
        var s = AutoDone() with { Filings = new FilingFacts(9, FilingBatchStatus.Completed, null, OutstandingChunks: 3, FailedChunks: 0) };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineStageStateKind.Running, State(d, PipelineStage.Filings));
        Assert.Equal(PipelineOutcome.CoreReady, d.Outcome);
    }

    [Fact]
    public void Litigation_policy_on_but_integration_not_configured_is_a_warning_skip()
    {
        var d = PipelineDecider.Decide(ManualDone(), new PipelinePolicy { LitigationSearch = true, LitigationAnalysis = true });

        Assert.Equal(PipelineStageSkipKind.Warning, d.Stages[PipelineStage.Litigation].SkipKind);
        Assert.Equal("UPSTREAM_SKIPPED", d.Stages[PipelineStage.LitigationAnalysis].ReasonCode);
        Assert.Equal(PipelineOutcome.CompleteWithWarnings, d.Outcome);
    }

    [Fact]
    public void Litigation_policy_on_and_configured_reports_what_enforce_mode_would_start()
    {
        var d = PipelineDecider.Decide(ManualDone() with { LitigationConfigured = true },
            new PipelinePolicy { LitigationSearch = true, LitigationAnalysis = true });

        Assert.Equal(PipelineStageStateKind.NotStarted, State(d, PipelineStage.Litigation));
        Assert.Equal("WOULD_START", d.Stages[PipelineStage.Litigation].ReasonCode);
        Assert.Equal("AWAITING_LITIGATION", d.Stages[PipelineStage.LitigationAnalysis].ReasonCode);
        Assert.Equal(PipelineOutcome.CoreReady, d.Outcome);
    }

    [Fact]
    public void A_manually_started_litigation_search_is_tracked_even_with_the_policy_off()
    {
        var s = ManualDone() with
        {
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.InProgress)
        };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineStageStateKind.Running, State(d, PipelineStage.Litigation));
        Assert.Equal("IMPORTING", d.Stages[PipelineStage.Litigation].ReasonCode);
        Assert.Equal(PipelineOutcome.CoreReady, d.Outcome);
    }

    [Fact]
    public void A_cancelled_request_is_cancelled()
    {
        var d = PipelineDecider.Decide(ManualDone() with { RequestStatus = RequestStatus.Cancelled }, PolicyOff);
        Assert.Equal(PipelineOutcome.Cancelled, d.Outcome);
        Assert.False(d.CoreReady);
    }

    /// <summary>For random fact combinations: every stage gets a verdict, a stage is never done while one it
    /// depends on isn't, and CoreReady matches the core stages exactly.</summary>
    [Fact]
    public void Dependency_edges_hold_for_generated_fact_combinations()
    {
        var rng = new Random(264);
        PipelineStage[][] edges =
        [
            [PipelineStage.Unlock, PipelineStage.Fetch],
            [PipelineStage.Refresh, PipelineStage.Fetch],
            [PipelineStage.Ingest, PipelineStage.Analysis],
            [PipelineStage.Analysis, PipelineStage.CalcAssurance],
            [PipelineStage.Analysis, PipelineStage.Dossier],
            [PipelineStage.CalcAssurance, PipelineStage.Dossier],
        ];

        for (var i = 0; i < 2000; i++)
        {
            var s = RandomSnapshot(rng);
            var policy = new PipelinePolicy { LitigationSearch = rng.Next(2) == 0, LitigationAnalysis = rng.Next(2) == 0 };
            var d = PipelineDecider.Decide(s, policy);

            Assert.Equal(Enum.GetValues<PipelineStage>().Length, d.Stages.Count);
            foreach (var edge in edges)
                if (!d.Stages[edge[0]].IsDone)
                    Assert.False(d.Stages[edge[1]].IsDone, $"trial {i}: {edge[1]} done while {edge[0]} is {d.Stages[edge[0]].State}");
            if (s.AutoFetch is not null && !d.Stages[PipelineStage.Fetch].IsDone && s.LatestCompletedIngestionRunId is null)
                Assert.False(d.Stages[PipelineStage.Ingest].IsDone);

            var coreDone = PipelineOutcomeCalculator.CoreStages.All(st => d.Stages[st].IsDone);
            Assert.Equal(coreDone && s.RequestStatus != RequestStatus.Cancelled, d.CoreReady);
        }
    }

    private static T Pick<T>(Random rng) where T : struct, Enum { var v = Enum.GetValues<T>(); return v[rng.Next(v.Length)]; }
    private static T? Maybe<T>(Random rng, Func<T> make) where T : class => rng.Next(3) == 0 ? null : make();

    private static PipelineSnapshot RandomSnapshot(Random rng)
    {
        var ingested = rng.Next(2) == 0;
        return new PipelineSnapshot
        {
            RequestId = 1,
            Cin = rng.Next(4) == 0 ? null : "U45203OR1995PLC003982",
            RequestStatus = Pick<RequestStatus>(rng),
            IsManualReviewRequired = rng.Next(4) == 0,
            AutoFetch = Maybe(rng, () => new AutoFetchFacts(5, Pick<AutoFetchJobStatus>(rng), rng.Next(2) == 0 ? 7 : null, rng.Next(2) == 0, null, null)),
            RefreshGateEnabled = rng.Next(2) == 0,
            Lifecycle = Maybe(rng, () => new LifecycleFacts(Pick<CompanyReportLifecycleState>(rng), rng.Next(2) == 0 ? DateTime.UtcNow : null, rng.Next(2) == 0)),
            LatestCompletedIngestionRunId = ingested ? 10 : null,
            HasIngestionWarnings = rng.Next(3) == 0,
            IngestionRunning = rng.Next(3) == 0,
            LatestIngestionStatus = rng.Next(4) == 0 ? null : Pick<IngestionRunStatus>(rng),
            Analysis = ingested ? Maybe(rng, () => new RunFacts<AnalysisRunStatus>(20, Pick<AnalysisRunStatus>(rng), null)) : null,
            CalcAssuranceMode = Pick<CalculationAssuranceMode>(rng),
            CalcAuditSnapshotId = rng.Next(2) == 0 ? 30 : null,
            CalcAiAuditStatus = rng.Next(3) == 0 ? null : Pick<CalculationAiAuditRunStatus>(rng),
            DossierHoldActive = rng.Next(3) == 0,
            Filings = Maybe(rng, () => new FilingFacts(9, Pick<FilingBatchStatus>(rng), null, rng.Next(3), rng.Next(2))),
            LitigationConfigured = rng.Next(2) == 0,
            LitigationSearch = Maybe(rng, () => new LitigationSearchFacts(40, Pick<LitigationSearchJobStatus>(rng), null, 41,
                rng.Next(3) == 0 ? null : Pick<LitigationReportSnapshotStatus>(rng))),
            LitigationAnalysis = Maybe(rng, () => new RunFacts<LitigationAiAnalysisRunStatus>(50, Pick<LitigationAiAnalysisRunStatus>(rng), null))
        };
    }
}
