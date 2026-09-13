using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Tests.Viz;

/// <summary>#116 (C6): <see cref="ChartPeriod"/> is factory-only — there is no public constructor to
/// bypass <see cref="ChartPeriod.ForFinancialYear"/>/<see cref="ChartPeriod.ForDate"/> at all (a
/// compile-time guarantee: a positional/record-struct constructor was deliberately avoided so a blank
/// label or a mismatched SortKey/ActualDate pair is not constructible, not just discouraged).</summary>
public class ChartPeriodTests
{
    [Fact]
    public void ForFinancialYear_uses_the_year_as_the_sort_key_and_has_no_actual_date()
    {
        var p = ChartPeriod.ForFinancialYear(2025);

        Assert.Equal("FY2025", p.Label);
        Assert.Equal(2025, p.SortKey);
        Assert.Null(p.ActualDate);
    }

    [Fact]
    public void ForDate_sort_key_is_always_the_real_day_number_never_caller_supplied()
    {
        var date = new DateOnly(2026, 9, 12);
        var p = ChartPeriod.ForDate(date, "12 Sep 2026");

        Assert.Equal("12 Sep 2026", p.Label);
        Assert.Equal(date.DayNumber, p.SortKey);
        Assert.Equal(date, p.ActualDate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForDate_rejects_a_blank_label(string? label)
    {
        Assert.Throws<ArgumentException>(() => ChartPeriod.ForDate(new DateOnly(2026, 1, 1), label!));
    }

    [Fact]
    public void Two_periods_with_the_same_label_sort_key_and_date_are_equal()
    {
        var a = ChartPeriod.ForDate(new DateOnly(2026, 1, 1), "1 Jan 2026");
        var b = ChartPeriod.ForDate(new DateOnly(2026, 1, 1), "1 Jan 2026");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void A_financial_year_period_and_a_date_period_are_never_equal_even_with_a_matching_sort_key()
    {
        var fy = ChartPeriod.ForFinancialYear(1); // SortKey 1
        var date = ChartPeriod.ForDate(DateOnly.MinValue, "day 1"); // DateOnly.MinValue.DayNumber == 1 too

        Assert.NotEqual(fy, date); // ActualDate differs (null vs populated) even though SortKey matches
    }
}
