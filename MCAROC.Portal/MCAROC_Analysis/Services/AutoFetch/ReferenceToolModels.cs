namespace MCAROC_Analysis.Services.AutoFetch;

/// <summary>What kind of failure a <see cref="ReferenceToolException"/> represents — replaces sniffing the
/// exception's message text (docs/pipeline-automation-plan.md §5.3). <see cref="AuthRejected"/> is
/// permanent (bad credentials — never retry, and it is what opens the circuit breaker immediately with no
/// threshold); <see cref="SessionExpired"/> is handled entirely inside <see
/// cref="ReferenceToolClient.WithSessionRecoveryAsync{T}"/> (a re-login + one replay, shared across every
/// caller via <see cref="ReferenceToolSession"/>) and should never reach a caller as this kind unless that
/// recovery itself failed; <see cref="CompanyLocked"/> is not detectable from any call in this file today — the reference
/// tool's own authoritative <c>getAssetTeams</c> call (docs/reference-tool-refresh-unlock-contract.md) is
/// what will actually produce it, wired in by #229/#266, not this issue; <see cref="RateLimited"/> and
/// <see cref="Unavailable"/> are both transient/retryable; <see cref="ContractChanged"/> means the response
/// parsed as JSON but not into a shape any known call expects — a developer, not an operator, needs to look;
/// <see cref="Other"/> is the fallback for anything not yet classified.</summary>
public enum ReferenceToolFailureKind
{
    Other,
    AuthRejected,
    SessionExpired,
    CompanyLocked,
    RateLimited,
    Unavailable,
    ContractChanged
}

/// <summary>Thrown for any reference-tool failure the job should surface to the user verbatim (session
/// expired, company not unlocked, non-Excel response…). <see cref="Retryable"/> marks transient HTTP
/// failures a per-file retry loop may re-attempt — derived from <see cref="Kind"/> when not given
/// explicitly, so a caller that only sets <see cref="Kind"/> still gets the right retry behavior.</summary>
public sealed class ReferenceToolException : Exception
{
    public ReferenceToolFailureKind Kind { get; }
    public bool Retryable { get; }

    public ReferenceToolException(string message, ReferenceToolFailureKind kind, Exception? inner = null) : base(message, inner)
    {
        Kind = kind;
        Retryable = kind is ReferenceToolFailureKind.RateLimited or ReferenceToolFailureKind.Unavailable;
    }

    /// <summary>Back-compat constructor for call sites not yet classified into a <see
    /// cref="ReferenceToolFailureKind"/> — maps the old boolean straight onto <see cref="Unavailable"/>/<see
    /// cref="Other"/> so existing behavior (retry transient HTTP failures, nothing else) is unchanged.</summary>
    public ReferenceToolException(string message, bool retryable = false, Exception? inner = null)
        : this(message, retryable ? ReferenceToolFailureKind.Unavailable : ReferenceToolFailureKind.Other, inner)
    {
    }
}

/// <summary>One auto-complete hit from the reference tool's company search.</summary>
public sealed record ReferenceCompanyHint(string LegalName, string Cin, string? Bid, string? Status, string? CompanyType);

/// <summary>Result of the session check: whether the configured cookie is still a logged-in session,
/// and the tool's numeric user id when the response carried one. <see cref="Kind"/> is only meaningful
/// when <see cref="IsValid"/> is false, and only set where the response actually lets it be classified
/// (a login attempt's own error field vs. a plain HTTP/parse failure) — null otherwise, not guessed.</summary>
public sealed record ReferenceSessionInfo(bool IsValid, string? UserId, string? Detail, ReferenceToolFailureKind? Kind = null);

/// <summary>Which of the tool's two workbook exports to download.</summary>
public enum ReferenceWorkbookKind
{
    /// <summary>The corporate master workbook (every sheet) — our MCA / ROC report.</summary>
    Corporate,
    /// <summary>The charge-details workbook — our Detailed Charge Report.</summary>
    Charge
}

public sealed record ReferenceDocumentAttachment(string Name, string AwsPath);

/// <summary>One filing in the tool's reference-documents registry: the main e-form PDF plus its
/// attachments. <see cref="DocId"/> is the tool's stable id for the filing (hex + version suffix).</summary>
public sealed record ReferenceDocument(
    string DocId,
    string Name,
    string? FormName,
    DateTimeOffset? DocumentDate,
    double? SizeKb,
    string AwsPath,
    string? Section,
    IReadOnlyList<ReferenceDocumentAttachment> Attachments);

/// <summary>One of the registry's four sections. <see cref="TotalCount"/> is what the tool reports it
/// holds; <see cref="Documents"/> is what it actually listed (the two differ when the tool pages).</summary>
public sealed record ReferenceDocumentSection(string Key, string FolderName, int TotalCount, IReadOnlyList<ReferenceDocument> Documents);

public sealed record ReferenceDocumentRegistry(IReadOnlyList<ReferenceDocumentSection> Sections)
{
    public int ListedCount => Sections.Sum(s => s.Documents.Count);
    public int TotalCount => Sections.Sum(s => s.TotalCount);
}
