using MCAROC_Analysis.Services.LitigationModule.Refresh;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class LiveProjectionEngineTests
{
    private static readonly DateTime T0 = new(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc);

    private static ParsedCaseSnapshot Snap(string? status = "PENDING", string? court = "DRT Mumbai 1", string? filing = "05-12-2022", string? last = "01-09-2026",
        string? next = "20-10-2026", string? decision = null, string? nature = null, string? suit = "5000000", int hearings = 10, string? stage = null) =>
        new(status, stage, court, filing, last, next, decision, nature, suit, hearings);

    private static ProjectionObservation Obs(long id, ParsedCaseSnapshot s, int minutes = 0, bool verified = true, ObservationOutcome outcome = ObservationOutcome.Found,
        ObservationSource source = ObservationSource.DrtPortal) => new(id, source, outcome, verified, T0.AddMinutes(minutes), s);

    private static LiveProjectionState Applied(params ProjectionObservation[] observations)
    {
        var state = LiveProjectionState.Empty;
        foreach (var o in observations) state = LiveProjectionEngine.Apply(state, o).State;
        return state;
    }

    [Fact]
    public void A_first_verified_observation_fills_the_projection_with_provenance()
    {
        var r = LiveProjectionEngine.Apply(LiveProjectionState.Empty, Obs(1, Snap()));
        Assert.Equal(ProjectionOutcome.Applied, r.Outcome);
        Assert.Equal(("PENDING", "DRT Mumbai 1", "20-10-2026", 10), (r.State.Status, r.State.Court, r.State.NextHearingDate, r.State.HearingCount));
        Assert.Equal("Portal", r.State.Sources[LiveProjectionEngine.FStatus].Source);
        Assert.Equal(1, r.State.Sources[LiveProjectionEngine.FStatus].ObservationId);
        Assert.Contains(LiveProjectionEngine.FStatus, r.ChangedFields);
    }

    [Theory]
    [InlineData(false, ObservationOutcome.Found)]
    [InlineData(true, ObservationOutcome.NotFound)]
    [InlineData(true, ObservationOutcome.Error)]
    [InlineData(true, ObservationOutcome.IdentityRejected)]
    public void Only_a_verified_found_observation_changes_the_projection(bool verified, ObservationOutcome outcome)
    {
        var before = Applied(Obs(1, Snap()));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(status: "DISPOSAL", court: "Other Court"), 5, verified, outcome));
        Assert.Equal(ProjectionOutcome.IgnoredNotVerified, r.Outcome);
        Assert.Same(before, r.State);
    }

    [Fact]
    public void A_late_observation_cannot_undo_newer_data()
    {
        var before = Applied(Obs(2, Snap(next: "25-10-2026"), minutes: 10));
        var r = LiveProjectionEngine.Apply(before, Obs(1, Snap(next: "20-10-2026"), minutes: 0));
        Assert.Equal(ProjectionOutcome.IgnoredLate, r.Outcome);
        Assert.Equal("25-10-2026", r.State.NextHearingDate);
    }

    [Fact]
    public void A_missing_value_never_erases_a_held_one()
    {
        var before = Applied(Obs(1, Snap()));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(court: null, filing: "NA", last: "", suit: null), 5));
        Assert.Equal("DRT Mumbai 1", r.State.Court);
        Assert.Equal("05-12-2022", r.State.FilingDate);
        Assert.Equal("01-09-2026", r.State.LastHearingDate);
        Assert.Equal("5000000", r.State.SuitAmount);     // response is partial (no filing date), so even the suit amount is kept
    }

    [Fact]
    public void A_complete_response_with_no_next_hearing_records_confirmed_empty_and_replaces_the_stale_date()
    {
        var before = Applied(Obs(1, Snap(next: "20-10-2026")));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(next: null), 5));
        Assert.Null(r.State.NextHearingDate);
        Assert.True(r.State.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);
        Assert.Contains(LiveProjectionEngine.FNextHearing, LiveProjectionEngine.ConfirmedEmptyMarks(r.State).Keys);

        // A later response that has it again fills it and clears the mark.
        var again = LiveProjectionEngine.Apply(r.State, Obs(3, Snap(next: "30-10-2026"), 10));
        Assert.Equal("30-10-2026", again.State.NextHearingDate);
        Assert.False(again.State.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);
    }

    [Fact]
    public void A_partial_response_never_counts_as_confirmed_empty()
    {
        var before = Applied(Obs(1, Snap(next: "20-10-2026")));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(status: null, filing: null, next: null), 5));
        Assert.Equal("20-10-2026", r.State.NextHearingDate);
        Assert.False(r.State.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);
    }

    [Fact]
    public void Decision_date_and_suit_amount_follow_the_portal_empty_rule_but_other_fields_do_not()
    {
        var before = Applied(Obs(1, Snap(decision: "01-01-2026", suit: "9000")));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(decision: null, suit: null, court: null), 5));
        Assert.Null(r.State.DecisionDate);
        Assert.Null(r.State.SuitAmount);
        Assert.Equal("DRT Mumbai 1", r.State.Court);     // court is not a meaningful-empty field
    }

    [Fact]
    public void One_response_that_moves_a_disposed_case_back_to_pending_is_held_and_a_second_agreeing_one_applies()
    {
        var disposed = Applied(Obs(1, Snap(status: "DISPOSAL", next: null, decision: "01-09-2026")));
        var first = LiveProjectionEngine.Apply(disposed, Obs(2, Snap(status: "PENDING"), 5));
        Assert.Equal(ProjectionOutcome.HeldStatusRegression, first.Outcome);
        Assert.Equal("DISPOSAL", first.State.Status);
        Assert.Equal("status:PENDING", first.State.HeldChange);

        var second = LiveProjectionEngine.Apply(first.State, Obs(3, Snap(status: "PENDING"), 10));
        Assert.Equal(ProjectionOutcome.Applied, second.Outcome);
        Assert.Equal("PENDING", second.State.Status);
        Assert.Null(second.State.HeldChange);
    }

    [Fact]
    public void A_held_regression_is_dropped_when_the_next_response_agrees_with_the_held_value()
    {
        var disposed = Applied(Obs(1, Snap(status: "DISPOSAL")));
        var held = LiveProjectionEngine.Apply(disposed, Obs(2, Snap(status: "PENDING"), 5));
        var back = LiveProjectionEngine.Apply(held.State, Obs(3, Snap(status: "DISPOSAL"), 10));
        Assert.Equal(ProjectionOutcome.Applied, back.Outcome);
        Assert.Equal("DISPOSAL", back.State.Status);
        Assert.Null(back.State.HeldChange);
    }

    [Fact]
    public void A_pending_case_becoming_disposed_applies_at_once() =>
        Assert.Equal(ProjectionOutcome.Applied, LiveProjectionEngine.Apply(Applied(Obs(1, Snap())), Obs(2, Snap(status: "DISPOSAL", next: null, decision: "05-10-2026"), 5)).Outcome);

    [Fact]
    public void A_shrinking_hearing_history_is_held_until_a_second_response_agrees()
    {
        var before = Applied(Obs(1, Snap(hearings: 10)));
        var first = LiveProjectionEngine.Apply(before, Obs(2, Snap(hearings: 4), 5));
        Assert.Equal(ProjectionOutcome.HeldShrinkingHistory, first.Outcome);
        Assert.Equal(10, first.State.HearingCount);

        var second = LiveProjectionEngine.Apply(first.State, Obs(3, Snap(hearings: 4), 10));
        Assert.Equal(ProjectionOutcome.Applied, second.Outcome);
        Assert.Equal(4, second.State.HearingCount);
    }

    [Fact]
    public void A_growing_history_applies_and_a_response_with_no_history_keeps_the_count()
    {
        var before = Applied(Obs(1, Snap(hearings: 10)));
        Assert.Equal(12, LiveProjectionEngine.Apply(before, Obs(2, Snap(hearings: 12), 5)).State.HearingCount);
        Assert.Equal(10, LiveProjectionEngine.Apply(before, Obs(2, Snap(hearings: 0), 5)).State.HearingCount);
    }

    [Fact]
    public void A_derived_decision_date_is_marked_derived()
    {
        var r = LiveProjectionEngine.Apply(LiveProjectionState.Empty,
            Obs(1, Snap(status: "ALLOWED", next: null, decision: "05-10-2026") with { DecisionDateDerived = true }, source: ObservationSource.ConsumerPortal));
        Assert.Equal("Derived", r.State.Sources[LiveProjectionEngine.FDecisionDate].Source);
    }

    [Fact]
    public void A_later_vendor_observation_does_not_overwrite_a_value_the_portal_supplied()
    {
        var before = Applied(Obs(1, Snap(status: "DISPOSAL", court: "DRT Mumbai 1", next: null, decision: "01-09-2026")));
        var r = LiveProjectionEngine.Apply(before, Obs(2, Snap(status: "PENDING", court: "Vendor Court Name", filing: "01-01-2020", next: "30-12-2026", decision: "02-02-2026"),
            10, source: ObservationSource.Vendor));
        Assert.Equal(ProjectionOutcome.Applied, r.Outcome);
        Assert.Equal(("DISPOSAL", "DRT Mumbai 1", "01-09-2026", "05-12-2022"), (r.State.Status, r.State.Court, r.State.DecisionDate, r.State.FilingDate));
        Assert.Equal("Portal", r.State.Sources[LiveProjectionEngine.FStatus].Source);
        Assert.Empty(r.ChangedFields);
    }

    [Fact]
    public void A_later_vendor_observation_does_not_resurrect_a_value_the_portal_confirmed_empty()
    {
        var emptied = Applied(Obs(1, Snap(next: "20-10-2026")), Obs(2, Snap(next: null), 5));
        Assert.True(emptied.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);

        var r = LiveProjectionEngine.Apply(emptied, Obs(3, Snap(next: "20-10-2026"), 10, source: ObservationSource.Vendor));
        Assert.Null(r.State.NextHearingDate);
        Assert.True(r.State.Sources[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);
    }

    [Fact]
    public void A_vendor_observation_still_fills_a_field_no_portal_has_spoken_about_and_never_blanks_a_portal_value()
    {
        var portal = Applied(Obs(1, Snap(court: null)));
        var r = LiveProjectionEngine.Apply(portal, Obs(2, Snap(court: "Vendor Court", next: null), 5, source: ObservationSource.Vendor));
        Assert.Equal("Vendor Court", r.State.Court);
        Assert.Equal("Vendor", r.State.Sources[LiveProjectionEngine.FCourt].Source);
        Assert.Equal("20-10-2026", r.State.NextHearingDate);
    }

    [Fact]
    public void A_vendor_report_of_pending_does_not_trigger_the_status_regression_hold_over_a_portal_disposal()
    {
        var disposed = Applied(Obs(1, Snap(status: "DISPOSAL", next: null)));
        var r = LiveProjectionEngine.Apply(disposed, Obs(2, Snap(status: "PENDING"), 5, source: ObservationSource.Vendor));
        Assert.Equal(ProjectionOutcome.Applied, r.Outcome);
        Assert.Equal("DISPOSAL", r.State.Status);
        Assert.Null(r.State.HeldChange);
    }

    [Fact]
    public void A_derived_decision_date_repeated_by_a_new_observation_moves_source_observation_and_time_together()
    {
        var derived = Snap(status: "ALLOWED", next: null, decision: "05-10-2026") with { DecisionDateDerived = true };
        var first = LiveProjectionEngine.Apply(LiveProjectionState.Empty, Obs(1, derived, 0, source: ObservationSource.ConsumerPortal));
        var again = LiveProjectionEngine.Apply(first.State, Obs(2, derived, 30, source: ObservationSource.ConsumerPortal));

        var source = again.State.Sources[LiveProjectionEngine.FDecisionDate];
        Assert.Equal(("Derived", 2L, T0.AddMinutes(30)), (source.Source, source.ObservationId, source.WhenUtc));
        Assert.Equal("05-10-2026", again.State.DecisionDate);
    }

    [Fact]
    public void A_derived_decision_date_never_replaces_one_the_portal_returned_explicitly()
    {
        var explicitDate = Applied(Obs(1, Snap(status: "DISPOSAL", next: null, decision: "01-09-2026")));
        var derived = Snap(status: "DISPOSAL", next: null, decision: "01-09-2026") with { DecisionDateDerived = true };
        var same = LiveProjectionEngine.Apply(explicitDate, Obs(2, derived, 5, source: ObservationSource.ConsumerPortal));
        Assert.Equal(("Portal", 1L), (same.State.Sources[LiveProjectionEngine.FDecisionDate].Source, same.State.Sources[LiveProjectionEngine.FDecisionDate].ObservationId));

        var different = LiveProjectionEngine.Apply(explicitDate, Obs(3, derived with { DecisionDate = "03-09-2026" }, 10, source: ObservationSource.ConsumerPortal));
        Assert.Equal("01-09-2026", different.State.DecisionDate);
        Assert.Equal("Portal", different.State.Sources[LiveProjectionEngine.FDecisionDate].Source);
    }

    [Fact]
    public void A_derived_decision_date_does_not_override_a_portal_confirmed_empty()
    {
        var emptied = Applied(Obs(1, Snap(status: "DISPOSAL", next: null, decision: "01-09-2026")), Obs(2, Snap(status: "DISPOSAL", next: null, decision: null), 5));
        Assert.True(emptied.Sources[LiveProjectionEngine.FDecisionDate].ConfirmedEmpty);

        var derived = Snap(status: "DISPOSAL", next: null, decision: "05-10-2026") with { DecisionDateDerived = true };
        var r = LiveProjectionEngine.Apply(emptied, Obs(3, derived, 10, source: ObservationSource.ConsumerPortal));
        Assert.Null(r.State.DecisionDate);
        Assert.True(r.State.Sources[LiveProjectionEngine.FDecisionDate].ConfirmedEmpty);
        Assert.Equal("Portal", r.State.Sources[LiveProjectionEngine.FDecisionDate].Source);
    }

    [Fact]
    public void A_vendor_observation_never_changes_the_hearing_count()
    {
        var portal = Applied(Obs(1, Snap(hearings: 10)));
        var larger = LiveProjectionEngine.Apply(portal, Obs(2, Snap(hearings: 25), 5, source: ObservationSource.Vendor));
        Assert.Equal(10, larger.State.HearingCount);
        var smaller = LiveProjectionEngine.Apply(portal, Obs(3, Snap(hearings: 2), 5, source: ObservationSource.Vendor));
        Assert.Equal(ProjectionOutcome.Applied, smaller.Outcome);          // not a shrink: the vendor has no history to compare
        Assert.Equal(10, smaller.State.HearingCount);
        Assert.Null(smaller.State.HeldChange);
    }

    [Fact]
    public void The_same_observation_applied_twice_changes_nothing_the_second_time()
    {
        var o = Obs(1, Snap());
        var once = LiveProjectionEngine.Apply(LiveProjectionState.Empty, o);
        var twice = LiveProjectionEngine.Apply(once.State, o);
        Assert.Empty(twice.ChangedFields);
        Assert.Equal(once.State.Status, twice.State.Status);
    }

    [Fact]
    public void Field_sources_survive_a_json_round_trip()
    {
        var state = Applied(Obs(1, Snap(next: null)));
        var back = LiveProjectionEngine.DeserializeSources(LiveProjectionEngine.SerializeSources(state.Sources));
        Assert.Equal(state.Sources.Keys.OrderBy(k => k), back.Keys.OrderBy(k => k));
        Assert.True(back[LiveProjectionEngine.FNextHearing].ConfirmedEmpty);
        Assert.Empty(LiveProjectionEngine.DeserializeSources("not json"));
    }
}
