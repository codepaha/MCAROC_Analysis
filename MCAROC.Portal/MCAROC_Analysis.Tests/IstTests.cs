using MCAROC_Analysis.Services;

namespace MCAROC_Analysis.Tests;

/// <summary>Every displayed time is IST (owner, 2026-09-24), independent of the server's clock zone.</summary>
public sealed class IstTests
{
    [Fact]
    public void Stored_utc_is_shown_as_ist_with_the_label()
    {
        var utc = new DateTime(2026, 9, 24, 3, 8, 34, DateTimeKind.Unspecified); // what EF hands back
        Assert.Equal("24 Sep 2026 08:38 IST", Ist.Format(utc));
        Assert.Equal("08:38:34 IST", Ist.Format(utc, "HH:mm:ss"));
    }

    [Fact]
    public void Late_utc_evening_is_the_next_day_in_ist()
    {
        var utc = new DateTime(2026, 12, 31, 18, 45, 0, DateTimeKind.Utc);
        Assert.Equal("1 Jan 2027", Ist.Date(utc));
        Assert.Equal("1 Jan 2027 00:15 IST", Ist.Format(utc));
    }

    [Fact]
    public void Local_kind_values_are_converted_from_the_server_zone_first()
    {
        var utc = new DateTime(2026, 9, 24, 3, 8, 0, DateTimeKind.Utc);
        Assert.Equal(Ist.Format(utc), Ist.Format(utc.ToLocalTime()));
    }

    [Fact]
    public void Offsets_and_nulls_are_handled()
    {
        Assert.Equal("24 Sep 2026 08:38 IST", Ist.Format(new DateTimeOffset(2026, 9, 24, 3, 8, 0, TimeSpan.Zero)));
        Assert.Equal("—", Ist.Format((DateTime?)null));
        Assert.Equal("N/A", Ist.Format((DateTime?)null, whenNull: "N/A"));
        Assert.Equal("-", Ist.Date((DateTime?)null, "yyyy-MM-dd", "-"));
    }

    [Fact]
    public void Now_uses_the_given_clock()
    {
        var clock = new FixedTime(new DateTimeOffset(2026, 9, 23, 20, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 9, 24, 1, 30, 0), Ist.Now(clock));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
