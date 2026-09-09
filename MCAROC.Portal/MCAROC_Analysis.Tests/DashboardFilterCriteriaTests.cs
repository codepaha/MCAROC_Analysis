using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Tests;

public class DashboardFilterCriteriaTests
{
    [Fact]
    public void ResolveWindow_DefaultsToTrailingNinetyDays_WhenBothUnset()
    {
        var filters = new DashboardFilterCriteria();
        var today = new DateOnly(2026, 6, 15);

        var (from, to) = filters.ResolveWindow(today);

        Assert.Equal(today, to);
        Assert.Equal(90, to.DayNumber - from.DayNumber + 1);
    }

    [Fact]
    public void ResolveWindow_UsesExplicitDatesWhenProvided()
    {
        var filters = new DashboardFilterCriteria { DateFrom = new DateOnly(2026, 1, 1), DateTo = new DateOnly(2026, 1, 31) };

        var (from, to) = filters.ResolveWindow(new DateOnly(2026, 6, 15));

        Assert.Equal(new DateOnly(2026, 1, 1), from);
        Assert.Equal(new DateOnly(2026, 1, 31), to);
    }

    [Fact]
    public void PriorPeriod_IsEqualLengthImmediatelyPreceding_NotGenericThirtyDaysBack()
    {
        // A 30-day custom range compares against the 30 days immediately before it, not a fixed "30
        // calendar days back" or "previous calendar month" — those are different concepts.
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 30); // 30-day window

        var (priorFrom, priorTo) = DashboardFilterCriteria.PriorPeriod(from, to);

        Assert.Equal(new DateOnly(2026, 1, 30), priorFrom);
        Assert.Equal(new DateOnly(2026, 2, 28), priorTo);
        Assert.Equal(to.DayNumber - from.DayNumber, priorTo.DayNumber - priorFrom.DayNumber); // same length
    }

    [Fact]
    public void ToRouteValues_OmitsUnsetFields()
    {
        var filters = new DashboardFilterCriteria { ClientId = 2 };

        var values = filters.ToRouteValues();

        Assert.Single(values);
        Assert.Equal("2", values["ClientId"]);
    }

    [Fact]
    public void ToRouteValues_OverrideAddsOrReplacesOneKey_CarryingRestForward()
    {
        var filters = new DashboardFilterCriteria { ClientId = 2, Priority = ReviewPriority.Medium };

        var values = filters.ToRouteValues(new() { ["Priority"] = "High" });

        Assert.Equal("2", values["ClientId"]);
        Assert.Equal("High", values["Priority"]); // overridden, not the original Medium
    }

    [Fact]
    public void ToRouteValues_EmptyOverrideValueRemovesTheKey()
    {
        var filters = new DashboardFilterCriteria { ClientId = 2 };

        var values = filters.ToRouteValues(new() { ["ClientId"] = "" });

        Assert.DoesNotContain("ClientId", values.Keys);
    }
}
