namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Configuration options for the DRT / DRAT portal client (plan section 4.9 and section 2.1).
/// Proxy pool and credentials live in configuration only, never in the repository or logs.</summary>
public sealed class DrtClientOptions
{
    public const string SectionName = "LitigationPortals:Drt";

    /// <summary>Base URL for DRT API calls, default <c>https://drt.gov.in/drtapi/</c>.</summary>
    public string BaseUrl { get; set; } = "https://drt.gov.in/drtapi/";

    /// <summary>Base URL for DRT order PDFs / viewers, default <c>https://cis.drt.gov.in/</c>.</summary>
    public string OrderBaseUrl { get; set; } = "https://cis.drt.gov.in/";

    /// <summary>The identifying User-Agent sent with each request, default <c>MCAROC-research/1.0</c>.</summary>
    public string UserAgent { get; set; } = "MCAROC-research/1.0";

    /// <summary>Target requests per second across this client instance (~0.5 req/s = ~2s between requests).</summary>
    public double RequestsPerSecond { get; set; } = 0.5;

    /// <summary>Per-call HTTP timeout in seconds, default 30s.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>Maximum call retries inside one attempt for transient failures, default 3.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Proxy URLs pool for rotation (e.g. <c>http://user:pass@proxy:port</c>). Credentials from config only.</summary>
    public string[] ProxyPool { get; set; } = [];

    /// <summary>How often to rotate proxy during continuous requests, default every 10 requests.</summary>
    public int ProxyRotationFrequency { get; set; } = 10;
}

