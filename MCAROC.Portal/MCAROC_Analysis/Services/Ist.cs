using System.Globalization;

namespace MCAROC_Analysis.Services;

/// <summary>Every timestamp the portal shows is Indian Standard Time (owner, 2026-09-24), whatever the server's own
/// clock zone. The database keeps UTC; convert only at display time, through here. Business calendar dates read
/// from source documents (filing, charge, rating dates) are dates, not instants, and are never converted.</summary>
public static class Ist
{
    public const string Label = "IST";

    public static readonly TimeZoneInfo Zone = FindZone();

    /// <summary>IST wall-clock time for a stored UTC instant. An <c>Unspecified</c> kind (what EF returns) is taken
    /// as UTC; a <c>Local</c> one is converted from the server's zone first.</summary>
    public static DateTime FromUtc(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(
        utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime FromUtc(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    /// <summary>The current IST wall-clock time.</summary>
    public static DateTime Now(TimeProvider? time = null) => FromUtc((time ?? TimeProvider.System).GetUtcNow());

    /// <summary>A timestamp for display, e.g. "24 Sep 2026 09:38 IST".</summary>
    public static string Format(DateTime utc, string format = "d MMM yyyy HH:mm") =>
        FromUtc(utc).ToString(format, CultureInfo.InvariantCulture) + " " + Label;

    public static string Format(DateTime? utc, string format = "d MMM yyyy HH:mm", string whenNull = "—") =>
        utc is { } value ? Format(value, format) : whenNull;

    public static string Format(DateTimeOffset instant, string format = "d MMM yyyy HH:mm") =>
        FromUtc(instant).ToString(format, CultureInfo.InvariantCulture) + " " + Label;

    public static string Format(DateTimeOffset? instant, string format = "d MMM yyyy HH:mm", string whenNull = "—") =>
        instant is { } value ? Format(value, format) : whenNull;

    /// <summary>The IST calendar date of an instant, e.g. "24 Sep 2026" — no label, since it is only a date.</summary>
    public static string Date(DateTime utc, string format = "d MMM yyyy") =>
        FromUtc(utc).ToString(format, CultureInfo.InvariantCulture);

    public static string Date(DateTime? utc, string format = "d MMM yyyy", string whenNull = "—") =>
        utc is { } value ? Date(value, format) : whenNull;

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Kolkata", "India Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        // India has no daylight saving, so a fixed offset is exact.
        return TimeZoneInfo.CreateCustomTimeZone(Label, TimeSpan.FromHours(5.5), "India Standard Time", "India Standard Time");
    }
}
