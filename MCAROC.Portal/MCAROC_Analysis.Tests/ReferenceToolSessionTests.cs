using MCAROC_Analysis.Services.AutoFetch;

namespace MCAROC_Analysis.Tests;

/// <summary>Pure logic tests for <see cref="ReferenceToolSession"/>'s sliding-hour login rate limiter and
/// generation bump — no HTTP, no database.</summary>
public sealed class ReferenceToolSessionTests
{
    [Fact]
    public void Allows_up_to_the_configured_cap_within_the_window()
    {
        var session = new ReferenceToolSession();
        var now = DateTime.UtcNow;

        Assert.True(session.TryReserveLoginSlot(3, now));
        Assert.True(session.TryReserveLoginSlot(3, now.AddMinutes(1)));
        Assert.True(session.TryReserveLoginSlot(3, now.AddMinutes(2)));
        Assert.False(session.TryReserveLoginSlot(3, now.AddMinutes(3))); // 4th attempt within the hour
    }

    [Fact]
    public void Slot_frees_up_once_the_oldest_attempt_leaves_the_one_hour_window()
    {
        var session = new ReferenceToolSession();
        var now = DateTime.UtcNow;
        Assert.True(session.TryReserveLoginSlot(1, now));
        Assert.False(session.TryReserveLoginSlot(1, now.AddMinutes(30))); // still within the hour

        Assert.True(session.TryReserveLoginSlot(1, now.AddHours(1).AddMinutes(1))); // first attempt has aged out
    }

    [Fact]
    public void SetCookie_bumps_generation_so_a_waiting_caller_can_detect_someone_else_already_refreshed()
    {
        var session = new ReferenceToolSession();
        var before = session.Generation;

        session.SetCookie("PHPSESSID=fresh", "265271");

        Assert.True(session.Generation > before);
        Assert.Equal("PHPSESSID=fresh", session.Cookie);
        Assert.Equal("265271", session.UserId);
    }

    [Fact]
    public void SeedFromConfiguredCookie_only_applies_when_nothing_has_been_set_yet()
    {
        var session = new ReferenceToolSession();
        session.SeedFromConfiguredCookie("PHPSESSID=configured");
        Assert.Equal("PHPSESSID=configured", session.Cookie);

        // A real login must win over the configured fallback, not be silently ignored.
        session.SetCookie("PHPSESSID=real-login", "1");
        session.SeedFromConfiguredCookie("PHPSESSID=configured"); // must not stomp the real session
        Assert.Equal("PHPSESSID=real-login", session.Cookie);
    }
}
