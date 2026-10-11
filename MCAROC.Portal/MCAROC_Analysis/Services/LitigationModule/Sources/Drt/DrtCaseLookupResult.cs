namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Status classification of a case lookup call per plan section 4.9.</summary>
public enum DrtLookupStatus
{
    /// <summary>Case details successfully fetched and parsed.</summary>
    Found,

    /// <summary>Empty HTTP 200 or validation error returned by the portal. Treated strictly as a candidate NotFound
    /// requiring confirmation across two independent exit addresses per plan section 4.9.</summary>
    NotFoundCandidate,

    /// <summary>Endpoint is unavailable or permanently returning 5xx (such as the RC/TRC endpoint).</summary>
    Unavailable,

    /// <summary>Permanent HTTP client error (400, 403, 404) or unrecoverable contract violation.</summary>
    Error
}

/// <summary>Result of looking up a case by number or diary number on DRT / DRAT.</summary>
/// <param name="Status">Lookup classification.</param>
/// <param name="Case">Case details when Found, null otherwise.</param>
/// <param name="Message">Descriptive message or reason.</param>
/// <param name="RawResponse">Raw response body when useful for diagnostics.</param>
public sealed record DrtCaseLookupResult(
    DrtLookupStatus Status,
    DrtCaseDetails? Case,
    string? Message = null,
    string? RawResponse = null)
{
    public bool IsFound => Status == DrtLookupStatus.Found && Case is not null;
    public bool IsNotFoundCandidate => Status == DrtLookupStatus.NotFoundCandidate;
    public bool IsUnavailable => Status == DrtLookupStatus.Unavailable;

    public static DrtCaseLookupResult Found(DrtCaseDetails caseDetails, string? raw = null) =>
        new(DrtLookupStatus.Found, caseDetails, null, raw);

    public static DrtCaseLookupResult NotFoundCandidate(string message = "Empty response from portal", string? raw = null) =>
        new(DrtLookupStatus.NotFoundCandidate, null, message, raw);

    public static DrtCaseLookupResult Unavailable(string message, string? raw = null) =>
        new(DrtLookupStatus.Unavailable, null, message, raw);

    public static DrtCaseLookupResult Error(string message, string? raw = null) =>
        new(DrtLookupStatus.Error, null, message, raw);
}

