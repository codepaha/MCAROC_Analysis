using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Dashboard;

namespace MCAROC_Analysis.Tests;

public class TrendBucketBuilderTests
{
    [Fact]
    public void ShortWindow_UsesWeeklyGranularity()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 1, 30); // 30 days

        Assert.Equal(TrendGranularity.Weekly, TrendBucketBuilder.GranularityFor(from, to));
    }

    [Fact]
    public void LongWindow_UsesMonthlyGranularity()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 4, 1); // 91 days

        Assert.Equal(TrendGranularity.Monthly, TrendBucketBuilder.GranularityFor(from, to));
    }

    [Fact]
    public void ExactlySixtyDays_StillWeekly()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(59); // 60-day inclusive window

        Assert.Equal(TrendGranularity.Weekly, TrendBucketBuilder.GranularityFor(from, to));
    }

    [Fact]
    public void SixtyOneDays_SwitchesToMonthly()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = from.AddDays(60); // 61-day inclusive window

        Assert.Equal(TrendGranularity.Monthly, TrendBucketBuilder.GranularityFor(from, to));
    }

    [Fact]
    public void EmptyBucketsAreEmittedAsZero_NoGapsInTheLine()
    {
        // 2026-01-05 is a Monday (ISO week start); this window covers exactly 3 whole ISO weeks
        // (Jan 5-11, 12-18, 19-25), so the bucket count is exact and unambiguous.
        var from = new DateOnly(2026, 1, 5);
        var to = new DateOnly(2026, 1, 25);
        var createdDates = new List<DateOnly> { new(2026, 1, 5) }; // only the first week has data

        var points = TrendBucketBuilder.Build(createdDates, from, to);

        Assert.Equal(3, points.Count);
        Assert.Equal(1, points[0].Created);
        Assert.Equal(0, points[1].Created);
        Assert.Equal(0, points[2].Created);
    }

    [Fact]
    public void WeeklyBuckets_GroupByIsoWeekStartingMonday()
    {
        var from = new DateOnly(2026, 1, 1); // Thursday
        var to = new DateOnly(2026, 1, 14);
        // 2026-01-05 is a Monday; 2026-01-08 is a Thursday in the same ISO week.
        var createdDates = new List<DateOnly> { new(2026, 1, 5), new(2026, 1, 8) };

        var points = TrendBucketBuilder.Build(createdDates, from, to);

        var weekOf5th = points.Single(p => p.BucketStart == new DateOnly(2026, 1, 5));
        Assert.Equal(2, weekOf5th.Created);
    }

    [Fact]
    public void MonthlyBuckets_GroupByCalendarMonth()
    {
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 3, 31);
        var createdDates = new List<DateOnly> { new(2026, 1, 15), new(2026, 1, 20), new(2026, 3, 1) };

        var points = TrendBucketBuilder.Build(createdDates, from, to);

        Assert.Equal(3, points.Count);
        Assert.Equal(2, points.Single(p => p.BucketStart == new DateOnly(2026, 1, 1)).Created);
        Assert.Equal(0, points.Single(p => p.BucketStart == new DateOnly(2026, 2, 1)).Created);
        Assert.Equal(1, points.Single(p => p.BucketStart == new DateOnly(2026, 3, 1)).Created);
    }
}
