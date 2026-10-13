using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Status classification of a case lookup call per plan section 4.9.</summary>
public enum ConsumerLookupStatus
{
    /// <summary>Case details successfully fetched and parsed.</summary>
    Found,

    /// <summary>Portal returned 400 "Case Details are not available for this case number!".
    /// Treated strictly as a candidate NotFound requiring concordance across two independent exit addresses.</summary>
    NotFoundCandidate,

    /// <summary>Endpoint is unavailable, permanent 5xx, or circuit breaker is open.</summary>
    Unavailable,

    /// <summary>Permanent HTTP client error (403, 404), exhausted retries, or unrecoverable contract violation.</summary>
    Error
}

/// <summary>Result of looking up a consumer case by case number on e-Jagriti (plan section 4.9).</summary>
public sealed record ConsumerCaseLookupResult(
    ConsumerLookupStatus Status,
    ConsumerCaseDetails? Case,
    string? Message = null,
    string? RawResponse = null,
    HttpStatusCode? StatusCode = null,
    string? ExitAddress = null)
{
    /// <summary>The wait the portal asked for with its last 429/503 (<c>Retry-After</c>), when it sent one. The shared host gate keeps every agent away for at least this long.</summary>
    public TimeSpan? RetryAfter { get; init; }

    public bool IsFound => Status == ConsumerLookupStatus.Found && Case is not null;
    public bool IsNotFoundCandidate => Status == ConsumerLookupStatus.NotFoundCandidate;
    public bool IsUnavailable => Status == ConsumerLookupStatus.Unavailable;
    public bool IsError => Status == ConsumerLookupStatus.Error;

    public static ConsumerCaseLookupResult Found(ConsumerCaseDetails caseDetails, string? raw = null, string? exitAddress = null) =>
        new(ConsumerLookupStatus.Found, caseDetails, null, raw, HttpStatusCode.OK, exitAddress);

    public static ConsumerCaseLookupResult NotFoundCandidate(
        string message = "Case Details are not available for this case number!", string? raw = null, string? exitAddress = null) =>
        new(ConsumerLookupStatus.NotFoundCandidate, null, message, raw, HttpStatusCode.BadRequest, exitAddress);

    public static ConsumerCaseLookupResult Unavailable(
        string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(ConsumerLookupStatus.Unavailable, null, message, raw, statusCode, exitAddress);

    public static ConsumerCaseLookupResult Error(
        string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(ConsumerLookupStatus.Error, null, message, raw, statusCode, exitAddress);

    /// <summary>Evaluates whether two candidate NotFound results are concordant across independent exit addresses per plan section 4.9.
    /// An empty or 400 answer becomes a confirmed NotFound only after two agreeing answers through different exit addresses.</summary>
    public static bool CanConfirmNotFound(ConsumerCaseLookupResult first, ConsumerCaseLookupResult second)
    {
        if (!first.IsNotFoundCandidate || !second.IsNotFoundCandidate) return false;
        if (string.IsNullOrWhiteSpace(first.ExitAddress) || string.IsNullOrWhiteSpace(second.ExitAddress)) return false;
        return !string.Equals(first.ExitAddress, second.ExitAddress, StringComparison.OrdinalIgnoreCase);
    }
}

