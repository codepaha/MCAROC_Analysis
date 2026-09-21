namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Connection settings for the BPR Litigation Data API (see
/// docs/litigation-data-lake-integration.md). Everything identifying the vendor — base URL, application
/// id, secret key — lives in user-secrets / environment variables, never in a committed file. The vendor's
/// own Postman collection embeds a live secret key and stale JWTs; neither is ever copied into source or
/// logs. The feature is inert until <see cref="IsConfigured"/>.</summary>
public sealed class BprLitigationOptions
{
    public const string SectionName = "BprLitigation";

    /// <summary>Scheme + host of the BPR API, e.g. "https://host.example:2087/". Required.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Application id sent as "id" to POST /sec/authenticate. Required.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Sent as "secret_key" to POST /sec/authenticate. Required.</summary>
    public string SecretKey { get; init; } = string.Empty;

    /// <summary>"entity_type" sent on job registration when the caller doesn't specify one. The vendor's
    /// own sample collection only demonstrates "individual" — corporate/LLP entity-type support is an
    /// unconfirmed part of the contract (see the doc's "Vendor contract required" section). Kept
    /// configurable rather than hard-coded so this can be corrected without a code change once confirmed.</summary>
    public string DefaultEntityType { get; init; } = "individual";

    /// <summary>"file_format" sent on job registration. The vendor's own captured example response
    /// returned an XLSX binary for a job registered with "file_format": "JSON" — a confirmed, unresolved
    /// discrepancy. <see cref="BprLitigationClient"/> sniffs the actual response instead of trusting this
    /// value, but it is still sent because omitting it is not a documented alternative.</summary>
    public string FileFormat { get; init; } = "JSON";

    public bool ExactMatch { get; init; } = true;
    public string Formats { get; init; } = "standard";

    /// <summary>Delay between polls of GET report/job/{id} while a job has not yet produced a recognizable
    /// terminal payload.</summary>
    public int PollIntervalSeconds { get; init; } = 30;

    /// <summary>Total wall-clock budget for polling one job before it is recorded Failed with a timeout
    /// reason. The vendor contract does not document an expected turnaround time.</summary>
    public int PollTimeoutMinutes { get; init; } = 30;

    /// <summary>Worker-level attempts (covering transient auth/network failures) before a job is recorded
    /// terminally Failed. Distinct from the in-poll-loop retry above.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Days after a report is retrieved that its orders' vendor PDF URLs are expected to remain
    /// retrievable — epic #239's confirmed product decision ("original vendor PDFs may expire after seven
    /// days"). A <see cref="Data.Entities.LitigationOrderDocument"/> stuck <see
    /// cref="Data.Entities.LitigationOrderDocumentStatus.Failed"/> past this window is moved to
    /// <see cref="Data.Entities.LitigationOrderDocumentStatus.Expired"/> instead of retried forever.</summary>
    public int OrderRetentionDays { get; init; } = 7;

    /// <summary>Hard cap on one order PDF's response size — the vendor contract does not document a file-size
    /// limit, so this guards against buffering an unbounded response into memory for a single order (a court
    /// order/judgment PDF is realistically a handful of MB at most). A response whose declared or actual size
    /// exceeds this is treated as a failed download, never silently truncated.</summary>
    public long MaxOrderPdfBytes { get; init; } = 50 * 1024 * 1024;

    /// <summary>Explicit allowlist of extra hostnames (beyond BPR's own configured host) that an order's
    /// <c>pdf_url</c> is allowed to point to — never inferred from the report content itself. A vendor
    /// report's <c>pdf_url</c> is untrusted input; without this, <see cref="BprLitigationClient.DownloadOrderDocumentAsync"/>
    /// would make the application fetch (and potentially retain, if the response happens to start with the
    /// PDF signature) whatever URL the report asserts — a server-side request forgery path onto internal
    /// services or cloud metadata endpoints. Empty by default: until an operator confirms a real vendor
    /// document/CDN host and adds it here, only BPR's own host is ever fetched from. Case-insensitive exact
    /// match only, no wildcards.</summary>
    public IReadOnlyList<string> AllowedOrderDocumentHosts { get; init; } = [];

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(SecretKey);
}
