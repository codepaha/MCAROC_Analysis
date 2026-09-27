using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

/// <summary>#292 (plan §6.3): a stage the decider finds genuinely stuck — claimed by a worker that then
/// stopped making progress without ever reaching its own failure path — reports NeedsAttention(STAGE_STALLED)
/// instead of sitting Running forever. Every case here also checks the negative: no signal (a null
/// heartbeat/lease, as every pre-#292 snapshot has) never trips the check regardless of the clock.</summary>
public sealed class PipelineStallDetectionTests
{
    private static readonly PipelinePolicy PolicyOff = new();
    private static readonly PipelineStallOptions DefaultStall = new(); // Fetch 20 min, Analysis 15 min.
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static PipelineSnapshot ManualDone() => new()
    {
        RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.AnalysisCompleted,
        LatestCompletedIngestionRunId = 10, LatestIngestionStatus = IngestionRunStatus.CompletedClean,
        Analysis = new(20, AnalysisRunStatus.Completed, null), CalcAssuranceMode = CalculationAssuranceMode.Off
    };

    [Fact]
    public void A_fetch_job_silent_for_longer_than_the_threshold_needs_attention_as_stalled()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null,
                HeartbeatUtc: Now.AddMinutes(-21))
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Fetch].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.Fetch].ReasonCode);
    }

    [Fact]
    public void A_fetch_job_with_a_recent_heartbeat_is_still_just_running()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null,
                HeartbeatUtc: Now.AddMinutes(-5))
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Fetch].State);
    }

    [Fact]
    public void A_fetch_job_with_no_heartbeat_recorded_is_never_flagged_as_stalled_no_matter_the_clock()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null)
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now.AddYears(10), DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Fetch].State);
    }

    [Theory]
    [InlineData(AutoFetchJobStatus.Queued)]
    [InlineData(AutoFetchJobStatus.WaitingForRefresh)]
    [InlineData(AutoFetchJobStatus.WaitingForUnlock)]
    public void A_parked_fetch_job_is_never_flagged_as_stalled_even_with_a_very_old_heartbeat(AutoFetchJobStatus parked)
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, parked, null, true, null, null, HeartbeatUtc: Now.AddDays(-30))
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.NotEqual("STAGE_STALLED", d.Stages[PipelineStage.Fetch].ReasonCode);
    }

    [Fact]
    public void Filing_downloads_stalled_after_the_main_export_finished_are_flagged_on_the_filings_stage()
    {
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, 7, true, null, null, Now.AddMinutes(-25))
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Filings].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.Filings].ReasonCode);
        // Fetch itself already reported Ok once RocDocumentId was set — the stall is specific to the
        // filing-download phase, not a re-flagging of the stage that already succeeded.
        Assert.Equal(PipelineStageStateKind.Succeeded, d.Stages[PipelineStage.Fetch].State);
    }

    [Fact]
    public void Genuinely_awaiting_the_main_fetch_is_never_mistaken_for_a_filings_stall()
    {
        // No RocDocumentId yet: whatever the clock says, this is Fetch's own stall to catch, not Filings'.
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null, HeartbeatUtc: Now.AddDays(-1))
        };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal("AWAITING_FETCH", d.Stages[PipelineStage.Filings].ReasonCode);
    }

    [Fact]
    public void An_analysis_run_active_far_longer_than_a_normal_pass_needs_attention_as_stalled()
    {
        var s = ManualDone() with { Analysis = new(20, AnalysisRunStatus.Running, null, StartedUtc: Now.AddMinutes(-16)) };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Analysis].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.Analysis].ReasonCode);
    }

    [Fact]
    public void An_analysis_run_still_within_its_normal_running_time_is_not_stalled()
    {
        var s = ManualDone() with { Analysis = new(20, AnalysisRunStatus.Running, null, StartedUtc: Now.AddMinutes(-3)) };
        var d = PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Analysis].State);
    }

    [Fact]
    public void An_analysis_run_with_no_started_time_recorded_is_never_flagged_as_stalled()
    {
        var s = ManualDone() with { Analysis = new(20, AnalysisRunStatus.Running, null) };
        var d = PipelineDecider.Decide(s, PolicyOff, Now.AddYears(10), DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Analysis].State);
    }

    [Fact]
    public void A_litigation_search_whose_lease_expired_while_still_polling_needs_attention_as_stalled()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Polling, null, null, null,
                LeaseExpiresUtc: Now.AddMinutes(-1))
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Litigation].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.Litigation].ReasonCode);
    }

    [Fact]
    public void A_litigation_search_with_a_lease_still_valid_is_just_running()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Polling, null, null, null,
                LeaseExpiresUtc: Now.AddMinutes(1))
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Litigation].State);
    }

    [Fact]
    public void A_litigation_search_with_no_lease_recorded_is_never_flagged_as_stalled()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Polling, null, null, null)
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now.AddYears(10), DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Litigation].State);
    }

    /// <summary>PR #310 review: the import that runs after the search job completes is a separate crash-safe
    /// unit of work with its own lease on LitigationReportSnapshot — checking only the search job's own
    /// (already-released) lease missed exactly the crash this feature exists to catch.</summary>
    [Fact]
    public void A_report_import_whose_own_lease_expired_while_still_importing_needs_attention_as_stalled()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.InProgress,
                LeaseExpiresUtc: Now.AddDays(-1), // the search job's own lease — long since released, irrelevant to this check
                SnapshotLeaseExpiresUtc: Now.AddMinutes(-1))
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.Litigation].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.Litigation].ReasonCode);
        Assert.Equal(41, d.Stages[PipelineStage.Litigation].SourceRef);
    }

    [Fact]
    public void A_report_import_with_a_lease_still_valid_is_just_importing()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.InProgress,
                SnapshotLeaseExpiresUtc: Now.AddMinutes(1))
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Litigation].State);
        Assert.Equal("IMPORTING", d.Stages[PipelineStage.Litigation].ReasonCode);
    }

    [Fact]
    public void A_report_import_with_no_lease_recorded_is_never_flagged_as_stalled()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.InProgress)
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true }, Now.AddYears(10), DefaultStall);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Litigation].State);
    }

    [Fact]
    public void A_litigation_analysis_run_whose_lease_expired_needs_attention_as_stalled()
    {
        var s = ManualDone() with
        {
            LitigationConfigured = true,
            LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.Completed),
            LitigationAnalysis = new RunFacts<LitigationAiAnalysisRunStatus>(50, LitigationAiAnalysisRunStatus.InProgress, null,
                LeaseExpiresUtc: Now.AddMinutes(-1))
        };
        var d = PipelineDecider.Decide(s, new PipelinePolicy { LitigationSearch = true, LitigationAnalysis = true }, Now, DefaultStall);

        Assert.Equal(PipelineStageStateKind.NeedsAttention, d.Stages[PipelineStage.LitigationAnalysis].State);
        Assert.Equal("STAGE_STALLED", d.Stages[PipelineStage.LitigationAnalysis].ReasonCode);
    }

    [Fact]
    public void A_custom_threshold_is_actually_honoured_not_just_the_default()
    {
        var tight = new PipelineStallOptions { FetchMinutes = 2, AnalysisMinutes = 1 };
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null, HeartbeatUtc: Now.AddMinutes(-3))
        };
        // Same facts, default (20 min) threshold: not stalled.
        Assert.Equal(PipelineStageStateKind.Running, PipelineDecider.Decide(s, PolicyOff, Now, DefaultStall).Stages[PipelineStage.Fetch].State);
        // Tightened (2 min) threshold: stalled.
        Assert.Equal(PipelineStageStateKind.NeedsAttention, PipelineDecider.Decide(s, PolicyOff, Now, tight).Stages[PipelineStage.Fetch].State);
    }

    [Fact]
    public void Omitting_the_clock_and_thresholds_still_works_and_never_stalls_facts_with_no_heartbeat()
    {
        // Every pre-#292 call site omits both new parameters; this is the compatibility guarantee that keeps
        // ~40 existing call sites across the test suite compiling and passing unchanged.
        var s = new PipelineSnapshot
        {
            RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.Created,
            AutoFetch = new AutoFetchFacts(5, AutoFetchJobStatus.CheckingSession, null, true, null, null)
        };
        var d = PipelineDecider.Decide(s, PolicyOff);

        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.Fetch].State);
    }
}
