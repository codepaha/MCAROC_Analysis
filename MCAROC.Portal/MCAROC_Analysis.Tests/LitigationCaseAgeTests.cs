using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Tests;

/// <summary>The age of a litigation case: measured from the filing date when the record has one; otherwise a labelled lower bound from
/// the earliest order on file; otherwise only the registration year. Never a guessed date.</summary>
public class LitigationCaseAgeTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 2);
    private static string[] None => [];

    [Fact]
    public void A_pending_case_with_a_filing_date_is_measured_to_the_as_of_date()
    {
        var age = LitigationCaseAges.Compute("2021-04-26", null, "2021", LitigationCaseStatusBucket.Pending, None, AsOf);

        Assert.Equal((CaseAgeBasis.Filed, CaseAgeKind.Pending, 65), (age.Basis, age.Kind, age.Months));
        Assert.Equal("Pending for 5 years 5 months", age.Headline);
        Assert.False(age.IsLowerBound);
        Assert.Equal("Filed 26 Apr 2021", age.BasisNote);
    }

    [Fact]
    public void A_disposed_case_runs_to_its_decision_date_or_failing_that_its_last_order_on_file()
    {
        var decided = LitigationCaseAges.Compute("2019-12-11", "2022-03-15", null, LitigationCaseStatusBucket.Disposed, ["2019-12-20"], AsOf);
        var noDecision = LitigationCaseAges.Compute("2019-12-11", null, null, LitigationCaseStatusBucket.Disposed, ["2020-01-10", "2022-03-01"], AsOf);

        Assert.Equal("Ran for 2 years 3 months", decided.Headline);
        Assert.False(decided.EndIsLastOrderOnFile);
        Assert.Equal("Ran for 2 years 2 months", noDecision.Headline);
        Assert.True(noDecision.EndIsLastOrderOnFile);
        Assert.Contains("last order on file 1 Mar 2022", noDecision.BasisNote);
    }

    [Fact]
    public void A_disposed_case_with_no_decision_date_and_no_orders_has_no_duration()
    {
        var age = LitigationCaseAges.Compute("2019-12-11", null, null, LitigationCaseStatusBucket.Disposed, None, AsOf);

        Assert.Null(age.Months);
        Assert.Equal("Age not known", age.Headline);
    }

    [Fact]
    public void Without_a_filing_date_the_earliest_order_gives_a_labelled_lower_bound()
    {
        var age = LitigationCaseAges.Compute(null, null, "2017", LitigationCaseStatusBucket.Pending, ["2021-07-20", "2019-03-25", "2024-01-05"], AsOf);

        Assert.Equal(CaseAgeBasis.FirstOrderOnFile, age.Basis);
        Assert.True(age.IsLowerBound);
        Assert.Equal("Pending for at least 7 years 6 months", age.Headline);
        Assert.Contains("No filing date on record", age.BasisNote);
        Assert.Contains("first order on file 25 Mar 2019", age.BasisNote);
    }

    [Fact]
    public void With_only_a_year_no_age_is_computed_and_with_nothing_the_age_is_not_known()
    {
        var yearOnly = LitigationCaseAges.Compute(null, null, "2017", LitigationCaseStatusBucket.Pending, None, AsOf);
        var nothing = LitigationCaseAges.Compute(null, null, null, LitigationCaseStatusBucket.Pending, None, AsOf);

        Assert.Equal(("Registered in 2017", null), (yearOnly.Headline, yearOnly.Months));
        Assert.Equal("Age not known", nothing.Headline);
        Assert.Equal(CaseAgeBand.NotKnown, LitigationCaseAges.BandOf(yearOnly));
    }

    [Theory]
    [InlineData("2027")]       // a year after the as-of year is not a registration year
    [InlineData("1850")]
    [InlineData("abc")]
    public void An_implausible_registration_year_is_ignored(string year) =>
        Assert.Equal(CaseAgeBasis.Unknown, LitigationCaseAges.Compute(null, null, year, LitigationCaseStatusBucket.Pending, None, AsOf).Basis);

    [Fact]
    public void An_unknown_status_is_the_time_since_the_start_and_is_never_called_pending()
    {
        var age = LitigationCaseAges.Compute("2023-10-02", null, null, LitigationCaseStatusBucket.Unknown, None, AsOf);

        Assert.Equal("3 years since filing", age.Headline);
    }

    [Theory]
    [InlineData("2026-10-02", "2026-10-02", 0)]
    [InlineData("2026-01-31", "2026-02-28", 0)] // the day number has not been reached
    [InlineData("2026-01-15", "2026-02-15", 1)]
    [InlineData("2025-10-03", "2026-10-02", 11)]
    [InlineData("2025-10-02", "2026-10-02", 12)]
    public void Months_are_whole_calendar_months(string start, string end, int expected) =>
        Assert.Equal(expected, LitigationCaseAges.MonthsBetween(DateOnly.Parse(start), DateOnly.Parse(end)));

    [Theory]
    [InlineData(0, "less than a month")]
    [InlineData(1, "1 month")]
    [InlineData(12, "1 year")]
    [InlineData(13, "1 year 1 month")]
    [InlineData(65, "5 years 5 months")]
    public void Durations_read_naturally(int months, string expected) => Assert.Equal(expected, LitigationCaseAges.FormatMonths(months));

    [Theory]
    [InlineData("2021-04-26", "2021-04-26")]
    [InlineData("26-04-2021", "2021-04-26")]   // day first, as in Indian court records
    [InlineData("09-01-2025", "2025-01-09")]
    [InlineData("26/04/2021", "2021-04-26")]
    [InlineData("26 Apr 2021", "2021-04-26")]
    public void Dates_are_read_the_way_the_record_writes_them(string text, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), LitigationCaseAges.ParseDate(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("List on 06.09.18")]
    [InlineData("31-02-2021")]
    public void Anything_else_is_not_a_date(string? text) => Assert.Null(LitigationCaseAges.ParseDate(text));

    [Fact]
    public void A_start_after_the_end_gives_no_duration_rather_than_a_negative_one()
    {
        var age = LitigationCaseAges.Compute("2027-01-01", null, null, LitigationCaseStatusBucket.Pending, None, AsOf);

        Assert.Null(age.Months);
    }

    [Fact]
    public void The_profile_bands_only_pending_cases_and_finds_the_oldest_with_a_known_age()
    {
        var profile = new LitigationAgeProfile();
        profile.Add(1, LitigationCaseAges.Compute("2026-06-01", null, null, LitigationCaseStatusBucket.Pending, None, AsOf));   // 4 months
        profile.Add(2, LitigationCaseAges.Compute("2024-02-01", null, null, LitigationCaseStatusBucket.Pending, None, AsOf));   // 2y 8m
        profile.Add(3, LitigationCaseAges.Compute("2014-06-09", null, null, LitigationCaseStatusBucket.Pending, None, AsOf));   // 12y
        profile.Add(4, LitigationCaseAges.Compute("2019-03-25", null, "2019", LitigationCaseStatusBucket.Disposed, ["2019-04-01"], AsOf)); // disposed: not banded
        profile.Add(5, LitigationCaseAges.Compute(null, null, "2017", LitigationCaseStatusBucket.Pending, None, AsOf));         // year only

        Assert.Equal(4, profile.PendingCases);
        Assert.Equal(1, profile.PendingByBand[CaseAgeBand.UnderOneYear]);
        Assert.Equal(1, profile.PendingByBand[CaseAgeBand.OneToThreeYears]);
        Assert.Equal(1, profile.PendingByBand[CaseAgeBand.OverFiveYears]);
        Assert.Equal(1, profile.PendingByBand[CaseAgeBand.NotKnown]);
        Assert.Equal(3, profile.OldestPendingCaseId);
    }

    [Theory]
    [InlineData(0, CaseAgeBand.UnderOneYear)]
    [InlineData(11, CaseAgeBand.UnderOneYear)]
    [InlineData(12, CaseAgeBand.OneToThreeYears)]
    [InlineData(35, CaseAgeBand.OneToThreeYears)]
    [InlineData(36, CaseAgeBand.ThreeToFiveYears)]
    [InlineData(59, CaseAgeBand.ThreeToFiveYears)]
    [InlineData(60, CaseAgeBand.OverFiveYears)]
    public void Bands_follow_whole_months(int months, CaseAgeBand expected) =>
        Assert.Equal(expected, LitigationCaseAges.BandOf(new LitigationCaseAge(CaseAgeBasis.Filed, CaseAgeKind.Pending, null, null, months, false, null)));
}
