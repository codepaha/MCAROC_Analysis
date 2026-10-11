using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Refresh;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class WholeNamePartyMatcherTests
{
    private static readonly string[] Subject = ["Kotak Mahindra Bank Limited"];

    [Theory]
    [InlineData("KOTAK MAHINDRA BANK LIMITED")]
    [InlineData("Kotak Mahindra Bank Ltd.")]
    [InlineData("M/s. Kotak Mahindra Bank Ltd")]
    [InlineData("KOTAK MAHINDRA BANK LIMITED & ANR")]
    [InlineData("KOTAK MAHINDRA BANK LIMITED AND ORS")]
    [InlineData("Kotak Mahindra Bank Limited, through its Authorised Signatory")]
    [InlineData("Kotak Mahindra Bank Limited (In Liquidation)")]
    public void The_same_party_in_its_different_spellings_is_verified(string party) =>
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, Subject).Outcome);

    [Theory]
    [InlineData("KOTAK MAHINDRA BANK LIMITED EMPLOYEES COOPERATIVE SOCIETY")]
    [InlineData("KOTAK MAHINDRA BANK LIMITED EMPLOYEES UNION")]
    [InlineData("KOTAK MAHINDRA BANK LIMITED PRIME")]
    [InlineData("KOTAK MAHINDRA PRIME LIMITED")]
    [InlineData("KOTAK SECURITIES LIMITED")]
    [InlineData("MAHINDRA BANK LIMITED")]
    [InlineData("")]
    [InlineData(null)]
    public void A_party_that_only_contains_or_resembles_the_name_is_rejected(string? party) =>
        Assert.Equal(PartyMatchOutcome.Rejected, WholeNamePartyMatcher.Match(party, Subject).Outcome);

    [Theory]
    [InlineData("ALPHA LIMITED THROUGH INDUSTRIAL SERVICES PRIVATE LIMITED", "Alpha Limited")]
    [InlineData("ALPHA LIMITED THRU LOGISTICS PRIVATE LIMITED", "Alpha Limited")]
    [InlineData("ALPHA LIMITED REPRESENTED TRADING COMPANY", "Alpha Limited")]
    public void A_distinct_name_that_merely_contains_a_representation_word_is_not_the_subject(string party, string subject) =>
        Assert.NotEqual(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, [subject]).Outcome);

    [Theory]
    [InlineData("ALPHA AND ANOTHER INDUSTRIES LIMITED", "Alpha Industries Limited")]
    [InlineData("ALPHA & ORS INDUSTRIES LIMITED", "Alpha Industries Limited")]
    [InlineData("ALPHA AND OTHERS TRADING LIMITED", "Alpha Trading Limited")]
    public void An_and_another_in_the_middle_of_a_name_is_not_boilerplate(string party, string subject) =>
        Assert.NotEqual(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, [subject]).Outcome);

    [Theory]
    [InlineData("Alpha Industries Limited & Anr.")]
    [InlineData("Alpha Industries Limited, and others")]
    [InlineData("ALPHA INDUSTRIES LTD & ORS")]
    public void A_trailing_and_others_suffix_is_still_stripped(string party) =>
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, ["Alpha Industries Limited"]).Outcome);

    [Theory]
    [InlineData("Kotak Mahindra Bank Limited THR. ITS DIRECTOR")]
    [InlineData("Kotak Mahindra Bank Limited represented by its Manager")]
    [InlineData("Kotak Mahindra Bank Limited rep. by its Authorised Officer")]
    public void Recognised_representation_phrases_are_still_stripped(string party) =>
        Assert.Equal(PartyMatchOutcome.Verified, WholeNamePartyMatcher.Match(party, ["Kotak Mahindra Bank Limited"]).Outcome);

    [Fact]
    public void Extra_words_after_the_name_are_named_as_the_reason()
    {
        var r = WholeNamePartyMatcher.Match("KOTAK MAHINDRA BANK LIMITED EMPLOYEES COOPERATIVE SOCIETY", Subject);
        Assert.Equal("extra-words", r.Rule);
        Assert.Null(r.MatchedName);
    }

    [Fact]
    public void A_name_that_differs_only_in_its_legal_form_is_ambiguous_not_verified()
    {
        var r = WholeNamePartyMatcher.Match("COASTAL PROJECTS PRIVATE LIMITED", ["Coastal Projects Limited"]);
        Assert.Equal(PartyMatchOutcome.Ambiguous, r.Outcome);
        Assert.Equal("legal-form-differs", r.Rule);
    }

    [Fact]
    public void A_former_name_verifies_and_says_so()
    {
        var r = WholeNamePartyMatcher.Match("OLD NAME INDUSTRIES LTD", ["New Name Industries Limited"], ["Old Name Industries Limited"]);
        Assert.Equal(PartyMatchOutcome.Verified, r.Outcome);
        Assert.Equal("exact-former-name", r.Rule);
    }

    [Fact]
    public void The_best_name_wins_when_there_are_several()
    {
        var r = WholeNamePartyMatcher.Match("ABC INDUSTRIES LIMITED", ["ABC Industries Private Limited", "ABC Industries Limited"]);
        Assert.Equal(PartyMatchOutcome.Verified, r.Outcome);
    }
}

public class LiveRefreshEligibilityTests
{
    private static readonly DateOnly Today = new(2026, 10, 11);
    private static readonly DateTime Now = new(2026, 10, 11, 6, 0, 0, DateTimeKind.Utc);

    private static LiveRefreshCaseState Complete(string status = "PENDING", string? next = "20-10-2026", bool verified = true, bool discovered = false,
        IReadOnlyDictionary<string, DateTime>? confirmedEmpty = null) =>
        new(status, null, "DRT Mumbai 1", "Maharashtra", "Mumbai", "05-12-2022", "01-09-2026", next, CaseNumberCompleteness.Complete, verified, discovered, confirmedEmpty);

    private static LiveRefreshDecision Decide(LiveRefreshCaseState c) => LiveRefreshEligibility.Decide(c, Today, Now);

    [Fact]
    public void A_disposed_case_is_never_refreshed_even_with_missing_fields()
    {
        var c = Complete("DISPOSED", null) with { State = null, District = "", FilingDate = "NA" };
        Assert.Equal(LiveRefreshAction.Skip, Decide(c).Action);
    }

    [Fact]
    public void A_disposed_case_whose_orders_are_wanted_goes_to_the_orders_path_only_until_the_final_order_is_on_file()
    {
        var wanted = Complete("DISPOSED", null) with { OrdersRequested = true };
        Assert.Equal(LiveRefreshAction.FetchOrdersOnly, Decide(wanted).Action);
        Assert.Equal(LiveRefreshAction.Skip, Decide(wanted with { FinalOrderOnFile = true }).Action);
    }

    [Fact]
    public void A_pending_case_with_a_future_next_hearing_and_nothing_missing_is_skipped()
    {
        var d = Decide(Complete(next: "20-10-2026"));
        Assert.Equal(LiveRefreshAction.Skip, d.Action);
        Assert.Empty(d.MissingFields);
    }

    [Fact]
    public void A_hearing_exactly_today_is_not_fetched()
    {
        var d = Decide(Complete(next: "11-10-2026"));
        Assert.Equal(LiveRefreshAction.Skip, d.Action);
        Assert.Contains("today", d.Reason);
    }

    [Fact]
    public void A_hearing_that_has_passed_is_fetched()
    {
        var d = Decide(Complete(next: "10-10-2026"));
        Assert.Equal(LiveRefreshAction.FetchStatus, d.Action);
        Assert.Contains("passed", d.Reason);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("court")]
    [InlineData("filing date")]
    public void A_missing_required_field_is_fetched_even_when_the_hearing_is_in_the_future(string field)
    {
        var c = field switch
        {
            "state" => Complete() with { State = "NA" },
            "court" => Complete() with { Court = "" },
            _ => Complete() with { FilingDate = null }
        };
        var d = Decide(c);
        Assert.Equal(LiveRefreshAction.FetchStatus, d.Action);
        Assert.Contains(field, d.MissingFields);
    }

    [Fact]
    public void An_incomplete_case_number_counts_as_a_missing_field() =>
        Assert.Contains("case number", Decide(Complete() with { NumberCompleteness = CaseNumberCompleteness.Partial }).MissingFields);

    [Fact]
    public void A_case_never_verified_is_fetched_whatever_its_dates() =>
        Assert.Equal(LiveRefreshAction.FetchStatus, Decide(Complete(next: "20-10-2026", verified: false)).Action);

    [Fact]
    public void A_newly_discovered_case_gets_one_detail_call()
    {
        var d = Decide(Complete(verified: false, discovered: true));
        Assert.Equal(LiveRefreshAction.FetchStatus, d.Action);
        Assert.Contains("newly discovered", d.Reason);
    }

    [Fact]
    public void An_unknown_status_is_treated_as_pending()
    {
        Assert.Equal(LiveRefreshAction.FetchStatus, Decide(Complete("OTHER", "10-10-2026")).Action);
        Assert.Equal(LiveRefreshAction.FetchStatus, Decide(Complete("NA", "20-10-2026")).Action);   // a missing status is a missing required field
    }

    [Fact]
    public void A_pending_case_with_no_next_hearing_is_fetched_until_the_portal_confirms_it_empty_and_then_not_for_seven_days()
    {
        Assert.Equal(LiveRefreshAction.FetchStatus, Decide(Complete(next: null)).Action);

        var recently = new Dictionary<string, DateTime> { [LiveRefreshEligibility.NextHearing] = Now.AddDays(-2) };
        Assert.Equal(LiveRefreshAction.Skip, Decide(Complete(next: null, confirmedEmpty: recently)).Action);

        var old = new Dictionary<string, DateTime> { [LiveRefreshEligibility.NextHearing] = Now.AddDays(-8) };
        Assert.Equal(LiveRefreshAction.FetchStatus, Decide(Complete(next: null, confirmedEmpty: old)).Action);
    }

    [Fact]
    public void An_unreadable_next_hearing_date_is_a_missing_field() =>
        Assert.Contains("next hearing date", Decide(Complete(next: "sometime soon")).MissingFields);

    [Fact]
    public void A_disposed_case_does_not_need_a_next_hearing_date_to_be_complete() =>
        Assert.DoesNotContain("next hearing date", LiveRefreshEligibility.MissingRequired(Complete("DISPOSED", null), MCAROC_Analysis.Models.LitigationCaseStatusBucket.Disposed, Now));
}
