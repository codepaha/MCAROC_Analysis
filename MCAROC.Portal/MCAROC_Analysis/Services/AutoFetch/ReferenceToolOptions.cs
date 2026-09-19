namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>Connection settings for the reference tool that the auto-fetch flow pulls a company's
/// MCA/ROC workbooks and filing PDFs from. Everything identifying the vendor (base URL, session) lives in
/// user-secrets / environment variables, never in a committed file — see README "Configuring secrets".
/// The feature is inert (the auto-fetch page explains what's missing) until <see cref="IsConfigured"/>.</summary>
public sealed class ReferenceToolOptions
{
    public const string SectionName = "ReferenceTool";

    /// <summary>Scheme + host of the reference tool's web app, e.g. "https://host.example". Required.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>The raw <c>Cookie</c> request-header value copied from a browser session that is logged
    /// in to the reference tool (its session id, user token and selected-team cookies). Required. The
    /// tool has no API-key login, so this is the only credential: when it expires every auto-fetch job
    /// fails fast at the session check with a message saying to refresh it.</summary>
    public string SessionCookie { get; init; } = string.Empty;

    /// <summary>The reference tool's numeric id of the logged-in user — the PDF download endpoint
    /// requires it as a query parameter. Optional: resolved from the session check when left empty.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Client/app version query parameters the tool's front end sends on every call.</summary>
    public string ClientVersion { get; init; } = "3.1.6";
    public string AppVersion { get; init; } = "8.1.26";

    /// <summary>Parallel PDF downloads per job. The tool serves each PDF individually, so this is the
    /// only thing that bounds how hard one job hits it.</summary>
    public int DownloadConcurrency { get; init; } = 6;

    /// <summary>Per-file download attempts before the file is recorded as a warning and skipped.</summary>
    public int DownloadAttempts { get; init; } = 3;

    /// <summary>Default cap on documents taken from each of the 4 filing sections when the form leaves
    /// the field blank. 0 = every document the registry lists.</summary>
    public int DefaultMaxDocumentsPerSection { get; init; } = 0;

    /// <summary>Hard ceiling on any single downloaded response (one workbook export or one filing PDF),
    /// enforced while streaming — not after the fact. A locked/misbehaving reference-tool response, or a
    /// single unexpectedly huge filing, must never be allowed to write past this size to disk regardless
    /// of what any declared Content-Length said.</summary>
    public long MaxResponseBytes { get; init; } = 300_000_000L; // 300 MB

    /// <summary>Hard ceiling on the total bytes one auto-fetch job downloads across every filing PDF.
    /// Matches the same order of magnitude as LargeArchiveUpload:MaxUncompressedSizeBytes (the equivalent
    /// cumulative guard for a manually-uploaded archive) — an auto-fetch job assembles the same kind of
    /// archive from the other direction, and needs the same ceiling.</summary>
    public long MaxAggregateDownloadBytes { get; init; } = 21_474_836_480L; // 20 GiB

    /// <summary>How long a storage reservation for one job's downloads stays valid before it would be
    /// swept as abandoned on a future app restart. Generous on purpose — a large job over slow bandwidth
    /// can genuinely run for hours, and letting the reservation lapse mid-job would let a concurrent job
    /// believe that disk headroom is free while this job is still writing into it.</summary>
    public TimeSpan StorageReservationLifetime { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Whether the auto-fetch form pre-ticks "also fetch the filing PDFs".</summary>
    public bool IncludeFilingsByDefault { get; init; } = true;

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36";

    /// <summary>Optional credentials for automated login to the reference tool. When provided,
    /// the client automatically performs login and session cookie refreshment when the session expires.</summary>
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    public bool CanAutoLogin => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) &&
        (!string.IsNullOrWhiteSpace(SessionCookie) || CanAutoLogin);
}
