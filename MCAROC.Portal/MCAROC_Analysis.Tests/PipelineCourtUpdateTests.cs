using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Fetching;
using MCAROC_Analysis.Services.LitigationModule.Freshness;
using MCAROC_Analysis.Services.Pipeline;
using Xunit;

namespace MCAROC_Analysis.Tests;

/// <summary>The court update stage (#444): it starts itself once the litigation search is done, nothing waits for it, and a run that never moves is shown as paused, never as done.</summary>
public sealed class PipelineCourtUpdateTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static readonly PipelinePolicy Policy = new() { LitigationSearch = true, CourtUpdate = true };

    private static PipelineSnapshot SearchDone(bool agents = true) => new()
    {
        RequestId = 1, Cin = "U45203OR1995PLC003982", RequestStatus = RequestStatus.AnalysisCompleted,
        LatestCompletedIngestionRunId = 10, LatestIngestionStatus = IngestionRunStatus.CompletedClean,
        Analysis = new(20, AnalysisRunStatus.Completed, null), CalcAssuranceMode = CalculationAssuranceMode.Off,
        LitigationConfigured = true, CourtAgentsEnabled = agents,
        LitigationSearch = new LitigationSearchFacts(40, LitigationSearchJobStatus.Completed, null, 41, LitigationReportSnapshotStatus.Completed)
    };

    private static RefreshRunProgress Run(DateTime? finished = null, int eligible = 10, int succeeded = 0, int failed = 0, int queued = 10, DateTime? heartbeat = null) =>
        new(7, 1, Now.AddMinutes(-30), finished, RefreshPhase.RefreshingCases, eligible, queued, 0, succeeded, failed, heartbeat);

    private static StageVerdict Court(PipelineSnapshot s, PipelinePolicy? policy = null) => PipelineDecider.Decide(s, policy ?? Policy, Now).Stages[PipelineStage.CourtUpdate];

    [Fact]
    public void It_is_ready_to_start_once_the_litigation_search_is_done()
    {
        var v = Court(SearchDone());
        Assert.Equal(PipelineStageStateKind.NotStarted, v.State);
        Assert.Equal(PipelineDecider.ReadyToStart, v.ReasonCode);
    }

    [Fact]
    public void It_waits_for_the_search_and_skips_when_the_search_is_skipped_or_the_policy_or_agents_are_off()
    {
        Assert.Equal("AWAITING_LITIGATION", Court(SearchDone() with { LitigationSearch = null }).ReasonCode);
        Assert.Equal("POLICY_OFF", Court(SearchDone(), new PipelinePolicy { LitigationSearch = true }).ReasonCode);
        var agentsOff = Court(SearchDone(agents: false));
        Assert.Equal(PipelineStageStateKind.Skipped, agentsOff.State);
        Assert.Equal("COURT_AGENTS_OFF", agentsOff.ReasonCode);
        Assert.Equal("UPSTREAM_SKIPPED", Court(SearchDone() with { LitigationConfigured = false, LitigationSearch = null }).ReasonCode);
    }

    [Fact]
    public void A_running_update_never_holds_back_the_core_track()
    {
        var d = PipelineDecider.Decide(SearchDone() with { CourtUpdate = Run(heartbeat: Now.AddMinutes(-1)) }, Policy, Now);
        Assert.Equal(PipelineStageStateKind.Running, d.Stages[PipelineStage.CourtUpdate].State);
        Assert.True(d.CoreReady);                                                                 // the dossier does not wait for the portals (plan 4.7)
        Assert.Equal(PipelineOutcome.CoreReady, d.Outcome);
    }

    [Fact]
    public void An_update_no_agent_reports_on_reads_as_paused_not_done()
    {
        var v = Court(SearchDone() with { CourtUpdate = Run(heartbeat: Now.AddMinutes(-11)) });
        Assert.Equal(PipelineStageStateKind.Running, v.State);
        Assert.Equal("COURT_UPDATE_PAUSED", v.ReasonCode);
        Assert.Equal("COURT_UPDATE_PAUSED", Court(SearchDone() with { CourtUpdate = Run(heartbeat: null) }).ReasonCode);   // started 30 min ago, never heard from
    }

    [Fact]
    public void A_finished_update_succeeds_and_a_high_failure_share_is_a_warning()
    {
        var clean = Court(SearchDone() with { CourtUpdate = Run(finished: Now, succeeded: 9, failed: 1, queued: 0) });
        Assert.Equal(PipelineStageStateKind.Succeeded, clean.State);
        Assert.Equal(7, clean.SourceRef);

        var partial = Court(SearchDone() with { CourtUpdate = Run(finished: Now, succeeded: 7, failed: 3, queued: 0) });
        Assert.Equal(PipelineStageStateKind.SucceededWithWarnings, partial.State);
        Assert.Equal("COURT_UPDATE_PARTIAL", partial.ReasonCode);
        Assert.Contains("3 of 10", partial.ReasonDetail);
    }

    [Fact]
    public void Enforce_mode_starts_it_only_with_its_own_switch()
    {
        var on = new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce, Enforce = new PipelineEnforceOptions { CourtUpdate = true } };
        Assert.True(on.EnforcesCourtUpdate());
        Assert.False(new PipelineOptions { Enabled = true, Mode = PipelineMode.Observe, Enforce = new PipelineEnforceOptions { CourtUpdate = true } }.EnforcesCourtUpdate());
        Assert.False(new PipelineOptions { Enabled = true, Mode = PipelineMode.Enforce }.EnforcesCourtUpdate());
    }

    private static PipelineSnapshot Analysed(bool reanalysisDue) => SearchDone() with
    {
        CourtUpdate = Run(finished: Now, succeeded: 10, queued: 0),
        LitigationAnalysis = new RunFacts<LitigationAiAnalysisRunStatus>(55, LitigationAiAnalysisRunStatus.Completed, null),
        LitigationReanalysisDue = reanalysisDue
    };

    private static readonly PipelinePolicy WithAnalysis = new() { LitigationSearch = true, LitigationAnalysis = true, CourtUpdate = true };

    [Fact]
    public void Post_login_a_court_update_that_changed_the_evidence_makes_the_analysis_due_again()
    {
        var v = PipelineDecider.Decide(Analysed(reanalysisDue: true), WithAnalysis, Now).Stages[PipelineStage.LitigationAnalysis];
        Assert.Equal(PipelineStageStateKind.NotStarted, v.State);
        Assert.Equal(PipelineDecider.ReadyToStart, v.ReasonCode);
        Assert.Contains("Re-analysis due", v.ReasonDetail);
    }

    [Fact]
    public void Without_changed_evidence_or_with_the_policy_off_a_completed_analysis_stays_done()
    {
        Assert.Equal(PipelineStageStateKind.Succeeded, PipelineDecider.Decide(Analysed(reanalysisDue: false), WithAnalysis, Now).Stages[PipelineStage.LitigationAnalysis].State);
        Assert.Equal(PipelineStageStateKind.Succeeded, PipelineDecider.Decide(Analysed(reanalysisDue: true), Policy, Now).Stages[PipelineStage.LitigationAnalysis].State);
    }
}
