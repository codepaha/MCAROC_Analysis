using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Freshness;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseFreshnessTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyList<string> NoFields = [];

    private static LiveRefreshDecision Skip(string reason = "next hearing in the future") => new(LiveRefreshAction.Skip, reason, NoFields);
    private static LiveRefreshDecision Due(string reason = "the next hearing date has passed") => new(LiveRefreshAction.FetchStatus, reason, NoFields);

    private static CaseFreshnessInput Case(LiveRefreshDecision eligibility, LitigationCaseStatusBucket bucket = LitigationCaseStatusBucket.Pending, bool merged = false, bool finalOrder = false,
        DateTime? verified = null, LastAttemptKind attempt = LastAttemptKind.None, DateTime? attemptAt = null, string? failure = null, bool queued = false, bool staleAnalysis = false) =>
        new(merged, bucket, finalOrder, eligibility, verified, attempt, attemptAt, failure, queued, staleAnalysis);

    [Fact]
    public void A_merged_case_is_excluded_from_every_view() =>
        Assert.Equal(FreshnessLabel.Excluded, CaseFreshness.Of(Case(Skip(), merged: true, verified: Now)).Label);

    [Fact]
    public void A_verified_case_that_is_not_due_reads_as_updated_with_the_date()
    {
        var r = CaseFreshness.Of(Case(Skip(), verified: Now.AddHours(-3), attempt: LastAttemptKind.Verified, attemptAt: Now.AddHours(-3)));
        Assert.Equal((FreshnessLabel.Updated, (DateTime?)Now.AddHours(-3)), (r.Label, r.AsOfUtc));
    }

    [Fact]
    public void A_verified_case_that_is_due_again_reads_as_not_yet_updated_never_as_updated()
    {
        var r = CaseFreshness.Of(Case(Due(), verified: Now.AddDays(-3), attempt: LastAttemptKind.Verified, attemptAt: Now.AddDays(-3)));
        Assert.Equal(FreshnessLabel.NotYetUpdated, r.Label);
        Assert.Null(r.AsOfUtc);
    }

    [Fact]
    public void A_never_checked_due_case_is_not_yet_updated_and_a_queued_one_says_queued()
    {
        Assert.Equal(FreshnessLabel.NotYetUpdated, CaseFreshness.Of(Case(Due())).Label);
        var queued = CaseFreshness.Of(Case(Skip(), queued: true));
        Assert.Equal((FreshnessLabel.NotYetUpdated, "queued"), (queued.Label, queued.Reason));
    }

    [Fact]
    public void A_pending_case_with_a_future_hearing_and_nothing_missing_is_not_due_unless_it_was_verified()
    {
        Assert.Equal(FreshnessLabel.NotDue, CaseFreshness.Of(Case(Skip("the next hearing is today: nothing new can have happened yet"))).Label);
        Assert.Equal(FreshnessLabel.Updated, CaseFreshness.Of(Case(Skip(), verified: Now.AddHours(-1))).Label);
    }

    [Fact]
    public void Disposed_cases_are_labelled_by_whether_their_final_order_is_on_file()
    {
        Assert.Equal(FreshnessLabel.DisposedFinalOrderOnFile, CaseFreshness.Of(Case(Skip("disposed"), LitigationCaseStatusBucket.Disposed, finalOrder: true)).Label);
        Assert.Equal(FreshnessLabel.DisposedNotRefreshed, CaseFreshness.Of(Case(Skip("disposed"), LitigationCaseStatusBucket.Disposed)).Label);
    }

    [Fact]
    public void A_disposed_case_wanted_for_its_orders_is_not_a_status_refresh_so_it_keeps_its_disposed_label()
    {
        var orders = new LiveRefreshDecision(LiveRefreshAction.FetchOrdersOnly, "disposed: orders requested", NoFields);
        Assert.Equal(FreshnessLabel.DisposedNotRefreshed, CaseFreshness.Of(Case(orders, LitigationCaseStatusBucket.Disposed)).Label);
    }

    [Theory]
    [InlineData(LastAttemptKind.NotFound, "the portal has no such case")]
    [InlineData(LastAttemptKind.IdentityRejected, "the portal returned a different case")]
    [InlineData(LastAttemptKind.Failed, "the portal could not be reached")]
    public void A_failed_or_unmatched_attempt_with_no_later_verification_could_not_be_verified(LastAttemptKind kind, string reason)
    {
        var r = CaseFreshness.Of(Case(Due(), attempt: kind, attemptAt: Now.AddHours(-1)));
        Assert.Equal((FreshnessLabel.CouldNotVerify, reason), (r.Label, r.Reason));
    }

    [Fact]
    public void A_stated_failure_reason_is_used_over_the_generic_one()
    {
        var r = CaseFreshness.Of(Case(Due(), attempt: LastAttemptKind.Failed, attemptAt: Now, failure: "failed after 5 attempts: timeout"));
        Assert.Equal("failed after 5 attempts: timeout", r.Reason);
    }

    [Fact]
    public void A_failure_after_an_earlier_success_could_not_be_verified_but_a_later_success_clears_it()
    {
        // verified at T-5h, then a failed retry at T-1h: the newest attempt failed and nothing verified after it
        var failedLater = CaseFreshness.Of(Case(Due(), verified: Now.AddHours(-5), attempt: LastAttemptKind.Failed, attemptAt: Now.AddHours(-1)));
        Assert.Equal(FreshnessLabel.CouldNotVerify, failedLater.Label);

        // a verification after the failure supersedes it
        var healed = CaseFreshness.Of(Case(Skip(), verified: Now.AddMinutes(-10), attempt: LastAttemptKind.Failed, attemptAt: Now.AddHours(-1)));
        Assert.Equal(FreshnessLabel.Updated, healed.Label);
    }

    [Fact]
    public void Stale_analysis_is_flagged_beside_the_data_label_whatever_that_label_is()
    {
        Assert.True(CaseFreshness.Of(Case(Skip(), verified: Now, staleAnalysis: true)).AnalysisOnEarlierData);
        Assert.True(CaseFreshness.Of(Case(Due(), staleAnalysis: true)).AnalysisOnEarlierData);
        Assert.True(CaseFreshness.Of(Case(Skip(), LitigationCaseStatusBucket.Disposed, staleAnalysis: true)).AnalysisOnEarlierData);
        Assert.False(CaseFreshness.Of(Case(Skip(), verified: Now)).AnalysisOnEarlierData);
    }

    [Fact]
    public void The_label_text_for_the_list_and_report_is_built_from_the_result()
    {
        string Ist(DateTime d) => d.ToString("dd MMM yyyy");
        Assert.Equal("Updated 12 Oct 2026", CaseFreshness.Text(CaseFreshness.Of(Case(Skip(), verified: Now)), Ist));
        Assert.Equal("Not yet updated", CaseFreshness.Text(CaseFreshness.Of(Case(Due())), Ist));
        Assert.Equal("Could not verify - the portal has no such case", CaseFreshness.Text(CaseFreshness.Of(Case(Due(), attempt: LastAttemptKind.NotFound, attemptAt: Now)), Ist));
        Assert.Equal(string.Empty, CaseFreshness.Text(CaseFreshness.Of(Case(Skip(), merged: true)), Ist));
    }
}

public class RefreshBannerTests
{
    private static readonly DateTime Now = new(2026, 10, 12, 6, 0, 0, DateTimeKind.Utc);

    private static RefreshProgressInput Input(int eligible, int handled, DateTime? started = null, IReadOnlyList<DateTime>? times = null, DateTime? heartbeat = null,
        string? failed = null, bool finished = false) =>
        new(started ?? Now.AddHours(-1), eligible, handled, times ?? [], heartbeat ?? Now.AddMinutes(-1), RefreshPhase.RefreshingCases, failed, finished);

    [Fact]
    public void No_run_means_no_banner() =>
        Assert.Equal(RefreshBannerState.Idle, RefreshBannerCalculator.Compute(new RefreshProgressInput(null, 0, 0, [], null, RefreshPhase.RefreshingCases, null, false), Now).State);

    [Fact]
    public void The_bar_counts_only_the_cases_that_were_eligible_when_the_run_started()
    {
        var b = RefreshBannerCalculator.Compute(Input(eligible: 6800, handled: 1240), Now);
        Assert.Equal((RefreshBannerState.Running, 18, 1240, 6800), (b.State, b.Percent, b.Handled, b.Total));
    }

    [Fact]
    public void The_estimate_comes_from_the_last_ten_minutes_of_speed_and_older_completions_are_ignored()
    {
        // 50 items finished in the last 10 minutes (5 per minute); 100 are left: 20 minutes. 500 older completions must not change that.
        var recent = Enumerable.Range(0, 50).Select(i => Now.AddMinutes(-9).AddSeconds(i * 10)).ToList();
        var old = Enumerable.Range(0, 500).Select(i => Now.AddHours(-3).AddSeconds(i)).ToList();
        var b = RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 100, times: old.Concat(recent).ToList()), Now);
        Assert.Equal(TimeSpan.FromMinutes(20), b.EstimatedRemaining);
    }

    [Fact]
    public void With_no_recent_speed_no_estimate_is_shown() =>
        Assert.Null(RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 100, times: [Now.AddHours(-2)]), Now).EstimatedRemaining);

    [Fact]
    public void With_no_agent_heard_for_ten_minutes_and_work_left_the_banner_says_paused_instead_of_freezing()
    {
        var b = RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 100, heartbeat: Now.AddMinutes(-11)), Now);
        Assert.Equal(RefreshBannerState.Paused, b.State);
        Assert.Contains("no fetch agent", b.Message);
        Assert.Null(b.EstimatedRemaining);
        Assert.Equal(RefreshBannerState.Running, RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 100, heartbeat: Now.AddMinutes(-9)), Now).State);
    }

    [Fact]
    public void A_run_that_never_had_a_heartbeat_is_judged_from_its_start()
    {
        var input = Input(eligible: 10, handled: 0, started: Now.AddMinutes(-30)) with { LastAgentHeartbeatUtc = null };
        Assert.Equal(RefreshBannerState.Paused, RefreshBannerCalculator.Compute(input, Now).State);
    }

    [Fact]
    public void A_failed_stage_says_so_with_its_reason_and_keeps_the_last_percent()
    {
        var b = RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 100, failed: "the portal is not responding"), Now);
        Assert.Equal((RefreshBannerState.Failed, 50), (b.State, b.Percent));
        Assert.Contains("the portal is not responding", b.Message);
    }

    [Fact]
    public void A_finished_run_or_one_with_everything_handled_is_completed_at_one_hundred_percent()
    {
        Assert.Equal((RefreshBannerState.Completed, 100), (RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 200), Now).State, RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 200), Now).Percent));
        Assert.Equal(RefreshBannerState.Completed, RefreshBannerCalculator.Compute(Input(eligible: 200, handled: 120, finished: true), Now).State);
    }

    [Fact]
    public void A_run_with_nothing_eligible_is_complete_at_once_not_divide_by_zero()
    {
        var b = RefreshBannerCalculator.Compute(Input(eligible: 0, handled: 0), Now);
        Assert.Equal((RefreshBannerState.Completed, 100), (b.State, b.Percent));
    }

    [Fact]
    public void Handled_beyond_the_total_is_clamped_so_the_bar_never_exceeds_one_hundred()
    {
        var b = RefreshBannerCalculator.Compute(Input(eligible: 100, handled: 140), Now);
        Assert.Equal((100, 100), (b.Handled, b.Percent));
    }
}
