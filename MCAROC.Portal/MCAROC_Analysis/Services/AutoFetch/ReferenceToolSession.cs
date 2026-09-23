namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>The reference tool's shared login state — registered as a **singleton**, unlike <see
/// cref="ReferenceToolClient"/> itself, which is transient (an <c>AddHttpClient&lt;T&gt;</c> registration).
/// Before this existed, the session cookie/signing key/user id were instance fields on that transient
/// client, so every DI scope (every job, every request) logged in separately — this is what actually shares
/// one session across concurrent callers. <see cref="Generation"/> lets a caller that already lost a race
/// to refresh the session skip a redundant login: read the generation before attempting, and only actually
/// log in if it is still the same generation by the time the single-flight gate is acquired.</summary>
public sealed class ReferenceToolSession
{
    private readonly object _sync = new();
    private readonly Queue<DateTime> _recentLoginsUtc = new();

    public SemaphoreSlim LoginGate { get; } = new(1, 1);
    public SemaphoreSlim SigningKeyGate { get; } = new(1, 1);

    public string? Cookie { get; private set; }
    public string? UserId { get; private set; }
    /// <summary>The tool's own user name for the session — its refresh request takes it as a parameter.</summary>
    public string? UserName { get; private set; }
    public byte[]? SigningKey { get; private set; }

    /// <summary>Bumped on every successful login. A caller that read this before waiting on <see
    /// cref="LoginGate"/> can compare it again once inside the gate: unchanged means genuinely log in now;
    /// changed means someone else already refreshed the session while this caller was waiting, so it can
    /// skip straight to using the new <see cref="Cookie"/> instead of logging in a second time.</summary>
    public int Generation { get; private set; }

    public void SetCookie(string cookie, string? userId)
    {
        lock (_sync)
        {
            Cookie = cookie;
            if (!string.IsNullOrWhiteSpace(userId)) UserId = userId;
            Generation++;
        }
    }

    public void SetUserName(string? userName)
    {
        if (string.IsNullOrWhiteSpace(userName)) return;
        lock (_sync) { UserName = userName.Trim(); }
    }

    public void SetSigningKey(byte[] key)
    {
        lock (_sync) { SigningKey = key; }
    }

    /// <summary>Seeds the session from configuration on first use — the operator-supplied cookie stands in
    /// until the tool invalidates it, at which point <see cref="SessionRecoveryHandler"/> logs in for real
    /// (if credentials are configured) and calls <see cref="SetCookie"/> itself.</summary>
    public void SeedFromConfiguredCookie(string cookie)
    {
        lock (_sync)
        {
            if (string.IsNullOrWhiteSpace(Cookie))
                Cookie = cookie;
        }
    }

    /// <summary>True when a login may be attempted right now without exceeding <paramref name="maxPerHour"/>
    /// — a sliding one-hour window, checked and recorded atomically under the same lock so two threads can
    /// never both believe they're under the limit for the same slot. Protects the account from a login loop
    /// (e.g. credentials that were valid yesterday and are now rejected) hammering it indefinitely.</summary>
    public bool TryReserveLoginSlot(int maxPerHour, DateTime nowUtc)
    {
        lock (_sync)
        {
            while (_recentLoginsUtc.Count > 0 && nowUtc - _recentLoginsUtc.Peek() > TimeSpan.FromHours(1))
                _recentLoginsUtc.Dequeue();
            if (_recentLoginsUtc.Count >= maxPerHour) return false;
            _recentLoginsUtc.Enqueue(nowUtc);
            return true;
        }
    }
}
