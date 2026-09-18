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

    /// <summary>Whether the auto-fetch form pre-ticks "also fetch the filing PDFs".</summary>
    public bool IncludeFilingsByDefault { get; init; } = true;

    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/153.0.0.0 Safari/537.36";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(SessionCookie);
}
