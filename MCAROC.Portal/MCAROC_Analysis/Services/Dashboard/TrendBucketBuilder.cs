using System.Globalization;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.Dashboard;

/// <summary>Buckets request-creation dates into a "Requests Created" trend series. Pure function, no DB —
/// directly unit-testable.</summary>
public static class TrendBucketBuilder
{
    private const int WeeklyGranularityThresholdDays = 60;

    public static TrendGranularity GranularityFor(DateOnly from, DateOnly to) =>
        (to.DayNumber - from.DayNumber + 1) <= WeeklyGranularityThresholdDays ? TrendGranularity.Weekly : TrendGranularity.Monthly;

    /// <summary>Buckets the given creation dates (already filtered to the request set the caller cares
    /// about) into ISO-week buckets when the window is short, calendar-month buckets otherwise. Every bucket
    /// in the window is emitted even when empty (Created = 0) so the line never has gaps — relevant given
    /// how few requests exist in a fresh/dev environment.</summary>
    public static List<TrendPoint> Build(IReadOnlyList<DateOnly> createdDates, DateOnly from, DateOnly to)
    {
        var granularity = GranularityFor(from, to);
        var bucketStarts = granularity == TrendGranularity.Weekly
            ? BuildWeeklyBucketStarts(from, to)
            : BuildMonthlyBucketStarts(from, to);

        var counts = new Dictionary<DateOnly, int>();
        foreach (var start in bucketStarts) counts[start] = 0;

        foreach (var date in createdDates)
        {
            var bucketStart = granularity == TrendGranularity.Weekly ? StartOfIsoWeek(date) : new DateOnly(date.Year, date.Month, 1);
            if (counts.ContainsKey(bucketStart)) counts[bucketStart]++;
        }

        return bucketStarts.Select(start => new TrendPoint(start, counts[start])).ToList();
    }

    private static DateOnly StartOfIsoWeek(DateOnly date)
    {
        var dayOfWeek = (int)date.DayOfWeek == 0 ? 7 : (int)date.DayOfWeek; // ISO: Monday = 1 ... Sunday = 7
        return date.AddDays(-(dayOfWeek - 1));
    }

    private static List<DateOnly> BuildWeeklyBucketStarts(DateOnly from, DateOnly to)
    {
        var starts = new List<DateOnly>();
        var current = StartOfIsoWeek(from);
        var lastWeekStart = StartOfIsoWeek(to);
        while (current <= lastWeekStart)
        {
            starts.Add(current);
            current = current.AddDays(7);
        }
        return starts;
    }

    private static List<DateOnly> BuildMonthlyBucketStarts(DateOnly from, DateOnly to)
    {
        var starts = new List<DateOnly>();
        var current = new DateOnly(from.Year, from.Month, 1);
        var last = new DateOnly(to.Year, to.Month, 1);
        while (current <= last)
        {
            starts.Add(current);
            current = current.AddMonths(1);
        }
        return starts;
    }

    public static string FormatLabel(DateOnly bucketStart, TrendGranularity granularity) =>
        granularity == TrendGranularity.Weekly
            ? bucketStart.ToString("MMM d", CultureInfo.InvariantCulture)
            : bucketStart.ToString("MMM yyyy", CultureInfo.InvariantCulture);
}
