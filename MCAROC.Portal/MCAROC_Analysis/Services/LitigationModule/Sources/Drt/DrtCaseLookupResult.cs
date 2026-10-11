using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Status classification of a case lookup call per plan section 4.9.</summary>
public enum DrtLookupStatus
{
    /// <summary>Case details successfully fetched and parsed.</summary>
    Found,

    /// <summary>Empty HTTP 200 or validation error returned by the portal. Treated strictly as a candidate NotFound
    /// requiring confirmation across two independent exit addresses per plan section 4.9.</summary>
    NotFoundCandidate,

    /// <summary>Endpoint is unavailable or permanently returning 5xx (such as the RC/TRC endpoint) or circuit breaker is open.</summary>
    Unavailable,

    /// <summary>Permanent HTTP client error (400, 403, 404), exhausted retries, or unrecoverable contract violation.</summary>
    Error
}

/// <summary>Result of looking up a case by number or diary number on DRT / DRAT.</summary>
/// <param name="Status">Lookup classification.</param>
/// <param name="Case">Case details when Found, null otherwise.</param>
/// <param name="Message">Descriptive message or reason.</param>
/// <param name="RawResponse">Raw response body when useful for diagnostics.</param>
/// <param name="StatusCode">HTTP status code received, if any.</param>
/// <param name="ExitAddress">Proxy exit address or direct interface through which the request exited.</param>
public sealed record DrtCaseLookupResult(
    DrtLookupStatus Status,
    DrtCaseDetails? Case,
    string? Message = null,
    string? RawResponse = null,
    HttpStatusCode? StatusCode = null,
    string? ExitAddress = null)
{
    public bool IsFound => Status == DrtLookupStatus.Found && Case is not null;
    public bool IsNotFoundCandidate => Status == DrtLookupStatus.NotFoundCandidate;
    public bool IsUnavailable => Status == DrtLookupStatus.Unavailable;
    public bool IsError => Status == DrtLookupStatus.Error;

    public static DrtCaseLookupResult Found(DrtCaseDetails caseDetails, string? raw = null, string? exitAddress = null) =>
        new(DrtLookupStatus.Found, caseDetails, null, raw, HttpStatusCode.OK, exitAddress);

    public static DrtCaseLookupResult NotFoundCandidate(
        string message = "Empty response from portal", string? raw = null, string? exitAddress = null) =>
        new(DrtLookupStatus.NotFoundCandidate, null, message, raw, HttpStatusCode.OK, exitAddress);

    public static DrtCaseLookupResult Unavailable(
        string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(DrtLookupStatus.Unavailable, null, message, raw, statusCode, exitAddress);

    public static DrtCaseLookupResult Error(
        string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(DrtLookupStatus.Error, null, message, raw, statusCode, exitAddress);

    /// <summary>Evaluates whether two candidate NotFound results are concordant across independent exit addresses per plan section 4.9.
    /// An empty 200 becomes a confirmed NotFound only after two agreeing answers through different exit addresses.</summary>
    public static bool CanConfirmNotFound(DrtCaseLookupResult first, DrtCaseLookupResult second)
    {
        if (!first.IsNotFoundCandidate || !second.IsNotFoundCandidate) return false;
        if (string.IsNullOrWhiteSpace(first.ExitAddress) || string.IsNullOrWhiteSpace(second.ExitAddress)) return false;
        return !string.Equals(first.ExitAddress, second.ExitAddress, StringComparison.OrdinalIgnoreCase);
    }
}
