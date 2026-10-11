using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Thread-safe proxy rotator managing a pool of proxy endpoints and exit address tracking (plan section 4.9).</summary>
public sealed class DrtProxyRotator : IWebProxy
{
    private readonly string[] _proxies;
    private readonly int _rotateFrequency;
    private int _index;
    private int _requestCount;
    private volatile string? _lastQueriedProxy;

    public DrtProxyRotator(string[]? proxies, int rotateFrequency = 10)
    {
        _proxies = proxies?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray() ?? [];
        _rotateFrequency = rotateFrequency > 0 ? rotateFrequency : 10;
    }

    /// <summary>Active exit address representation (either proxy URL/host or "direct").</summary>
    public string CurrentExit
    {
        get
        {
            if (_proxies.Length == 0) return "direct";
            var idx = Math.Abs(Volatile.Read(ref _index)) % _proxies.Length;
            return _proxies[idx];
        }
    }

    /// <summary>The exit address queried by the HTTP transport for the current request.</summary>
    public string? LastQueriedExit
    {
        get => _lastQueriedProxy;
        set => _lastQueriedProxy = value;
    }

    /// <summary>Total configured proxies in the pool.</summary>
    public int ProxyCount => _proxies.Length;

    /// <summary>Notifies the rotator of a request. Rotates to the next proxy when reaching the configured frequency.</summary>
    public void NotifyRequest()
    {
        if (_proxies.Length <= 1) return;
        var count = Interlocked.Increment(ref _requestCount);
        if (count % _rotateFrequency == 0)
        {
            Rotate();
        }
    }

    /// <summary>Forces rotation to the next proxy (e.g. on a transient retry or failure).</summary>
    public void Rotate()
    {
        if (_proxies.Length <= 1) return;
        Interlocked.Increment(ref _index);
    }

    internal static readonly AsyncLocal<HttpRequestMessage?> CurrentRequest = new();

    /// <inheritdoc />
    public Uri? GetProxy(Uri destination)
    {
        if (_proxies.Length == 0)
        {
            _lastQueriedProxy = "direct";
            if (CurrentRequest.Value is { } req0)
            {
                req0.Options.Set(DrtClient.ExitProvenanceKey, "direct");
            }
            return null;
        }
        var exit = CurrentExit;
        _lastQueriedProxy = exit;
        if (CurrentRequest.Value is { } req)
        {
            req.Options.Set(DrtClient.ExitProvenanceKey, exit);
        }
        return Uri.TryCreate(exit, UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <inheritdoc />
    public bool IsBypassed(Uri host) => false;

    /// <inheritdoc />
    public ICredentials? Credentials { get; set; }
}
