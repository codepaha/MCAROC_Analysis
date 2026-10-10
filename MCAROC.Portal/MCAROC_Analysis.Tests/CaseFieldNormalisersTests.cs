using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using Xunit;

namespace MCAROC_Analysis.Tests;

public class CaseFieldNormalisersTests
{
    private static readonly DateOnly Today = new(2026, 10, 10);

    [Theory]
    [InlineData("Disposed", LitigationCaseStatusBucket.Disposed)]
    [InlineData("Case disposed", LitigationCaseStatusBucket.Disposed)]
    [InlineData("DISPOSED OF", LitigationCaseStatusBucket.Disposed)]
    [InlineData("DISPOSED OFF", LitigationCaseStatusBucket.Disposed)]
    [InlineData("DISPOSAL", LitigationCaseStatusBucket.Disposed)]
    [InlineData("DISMISSED", LitigationCaseStatusBucket.Disposed)]
    [InlineData("ALLOWED", LitigationCaseStatusBucket.Disposed)]
    [InlineData("PENDING", LitigationCaseStatusBucket.Pending)]
    [InlineData("Pending", LitigationCaseStatusBucket.Pending)]
    [InlineData("Appearance", LitigationCaseStatusBucket.Pending)]
    [InlineData("Arguments", LitigationCaseStatusBucket.Pending)]
    [InlineData("Prosecution Evidence", LitigationCaseStatusBucket.Pending)]
    [InlineData("OTHER", LitigationCaseStatusBucket.Unknown)]
    [InlineData("NA", LitigationCaseStatusBucket.Unknown)]
    [InlineData("", LitigationCaseStatusBucket.Unknown)]
    [InlineData(null, LitigationCaseStatusBucket.Unknown)]
    public void Every_spelling_of_a_status_lands_in_one_bucket(string? status, LitigationCaseStatusBucket expected) =>
        Assert.Equal(expected, CaseStatusVocabulary.Map(status).Bucket);

    [Theory]
    [InlineData("ALLOWED")]
    [InlineData("DISMISSED")]
    public void An_outcome_word_is_kept_as_the_outcome(string status) => Assert.Equal(status, CaseStatusVocabulary.Map(status).Outcome);

    [Fact]
    public void A_stage_word_in_the_status_field_is_returned_to_be_moved_to_the_stage_field_only_when_that_is_empty()
    {
        Assert.Equal("Arguments", CaseStatusVocabulary.Map("Arguments").StageWord);
        Assert.Null(CaseStatusVocabulary.Map("Arguments", "Final Hearing").StageWord);
        Assert.Null(CaseStatusVocabulary.Map("PENDING").StageWord);
        Assert.Null(CaseStatusVocabulary.Map("DISPOSED").StageWord);
    }

    [Theory]
    [InlineData("25-12-2025", 2025, 12, 25)]
    [InlineData("25/12/2025", 2025, 12, 25)]
    [InlineData("03/04/2026", 2026, 4, 3)]          // day-first, never month-first
    [InlineData("3-4-2026", 2026, 4, 3)]
    [InlineData("2026-04-03", 2026, 4, 3)]
    public void Dates_are_read_day_first(string text, int y, int m, int d) => Assert.Equal(new DateOnly(y, m, d), CaseDates.ParseDate(text));

    [Theory]
    [InlineData("NA")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("00-00-0000")]
    [InlineData("not a date")]
    [InlineData("31/02/2026")]
    public void Placeholders_and_unreadable_dates_are_null(string? text) => Assert.Null(CaseDates.ParseDate(text));

    [Fact]
    public void A_utc_timestamp_late_in_the_day_is_the_next_ist_calendar_date() =>
        Assert.Equal(new DateOnly(2026, 4, 4), CaseDates.ParseDate("2026-04-03T20:30:00Z"));

    [Fact]
    public void A_future_date_on_a_pending_case_is_the_next_hearing()
    {
        var h = CombinedHearingDate.Split("20-10-2026", LitigationCaseStatusBucket.Pending, Today);
        Assert.Equal(new DateOnly(2026, 10, 20), h.Next);
        Assert.Null(h.Last);
    }

    [Fact]
    public void A_past_date_on_a_pending_case_is_only_the_last_hearing_never_the_next()
    {
        var h = CombinedHearingDate.Split("20-08-2026", LitigationCaseStatusBucket.Pending, Today);
        Assert.Null(h.Next);
        Assert.Equal(new DateOnly(2026, 8, 20), h.Last);
    }

    [Fact]
    public void Today_counts_as_a_next_hearing() =>
        Assert.Equal(Today, CombinedHearingDate.Split("10-10-2026", LitigationCaseStatusBucket.Unknown, Today).Next);

    [Fact]
    public void A_past_date_on_a_disposed_case_is_the_last_hearing_and_a_derived_decision_candidate()
    {
        var h = CombinedHearingDate.Split("20-08-2026", LitigationCaseStatusBucket.Disposed, Today);
        Assert.Equal(new DateOnly(2026, 8, 20), h.Last);
        Assert.Equal(new DateOnly(2026, 8, 20), h.DerivedDecision);
        Assert.Null(h.Next);
    }

    [Fact]
    public void A_future_date_on_a_disposed_case_is_not_trusted() =>
        Assert.Equal(new CombinedHearing(null, null, null), CombinedHearingDate.Split("20-12-2026", LitigationCaseStatusBucket.Disposed, Today));

    [Fact]
    public void No_date_gives_nothing() =>
        Assert.Equal(new CombinedHearing(null, null, null), CombinedHearingDate.Split("NA", LitigationCaseStatusBucket.Pending, Today));

    [Theory]
    [InlineData("PunjabHighCourt", "High Court of Punjab and Haryana")]
    [InlineData("High Court of Punjab and Haryana", "High Court of Punjab and Haryana")]
    [InlineData("Punjab and Haryana High Court", "High Court of Punjab and Haryana")]
    [InlineData("Some Unlisted Court", "Some Unlisted Court")]
    public void The_same_court_under_different_names_is_one_canonical_court(string raw, string expected) =>
        Assert.Equal(expected, CourtNames.Normalise(raw).Canonical);

    [Fact]
    public void The_state_and_list_type_suffix_is_split_off()
    {
        var p = CourtNames.Normalise("Civil Judge Senior Division,Taluka Court, Ajnala, Punjab, Civil Causelist");
        Assert.Equal("Civil Judge Senior Division,Taluka Court, Ajnala", p.Canonical);
        Assert.Equal("Punjab", p.State);
        Assert.Equal("Civil Causelist", p.ListType);
    }

    [Fact]
    public void A_name_without_a_suffix_has_no_state_or_list_type()
    {
        var p = CourtNames.Normalise("Sub Judge Kancheepuram");
        Assert.Null(p.State);
        Assert.Null(p.ListType);
    }

    [Fact]
    public void An_empty_court_name_is_empty() => Assert.Equal(string.Empty, CourtNames.Normalise("NA").Canonical);
}
