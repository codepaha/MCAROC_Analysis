using System.Net;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Status of a daily order or judgment document fetch.</summary>
public enum ConsumerOrderDownloadStatus
{
    /// <summary>Document successfully retrieved and decoded.</summary>
    Success,

    /// <summary>Portal confirms no order/judgment document exists for this hearing (HTTP 400 "Daily Order not available").</summary>
    NotFound,

    /// <summary>Server unavailable or circuit breaker open.</summary>
    Unavailable,

    /// <summary>Network, decoding, or permanent HTTP client error.</summary>
    Error
}

/// <summary>Downloaded order or judgment document from e-Jagriti (plan section 4.9).</summary>
/// <param name="FilingReferenceNumber">Numeric filing reference number.</param>
/// <param name="DateOfHearing">Calendar date of hearing (IST).</param>
/// <param name="OrderTypeId">Order type ID (1 for daily order).</param>
/// <param name="Bytes">Raw binary content (PDF or UTF-8 encoded HTML note).</param>
/// <param name="ContentType">"pdf" or "html".</param>
/// <param name="ExitAddress">Proxy exit address used for the download.</param>
public sealed record ConsumerOrderDocument(
    long FilingReferenceNumber,
    DateOnly DateOfHearing,
    int OrderTypeId,
    byte[] Bytes,
    string ContentType,
    string? ExitAddress = null)
{
    /// <summary>File extension including leading dot (".pdf" or ".html").</summary>
    public string FileExtension => ContentType == "html" ? ".html" : ".pdf";

    /// <summary>MIME content type header string.</summary>
    public string MimeType => ContentType == "html" ? "text/html" : "application/pdf";
}

/// <summary>Result container for daily order document downloads.</summary>
public sealed record ConsumerOrderDownloadResult(
    ConsumerOrderDownloadStatus Status,
    ConsumerOrderDocument? Document,
    string? Message = null,
    string? RawResponse = null,
    HttpStatusCode? StatusCode = null,
    string? ExitAddress = null)
{
    public bool IsSuccess => Status == ConsumerOrderDownloadStatus.Success && Document is not null;
    public bool IsNotFound => Status == ConsumerOrderDownloadStatus.NotFound;
    public bool IsUnavailable => Status == ConsumerOrderDownloadStatus.Unavailable;
    public bool IsError => Status == ConsumerOrderDownloadStatus.Error;

    public static ConsumerOrderDownloadResult Success(ConsumerOrderDocument document, string? raw = null, string? exitAddress = null) =>
        new(ConsumerOrderDownloadStatus.Success, document, null, raw, HttpStatusCode.OK, exitAddress);

    public static ConsumerOrderDownloadResult NotFound(string message = "Daily Order not available", string? raw = null, string? exitAddress = null) =>
        new(ConsumerOrderDownloadStatus.NotFound, null, message, raw, HttpStatusCode.BadRequest, exitAddress);

    public static ConsumerOrderDownloadResult Unavailable(string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(ConsumerOrderDownloadStatus.Unavailable, null, message, raw, statusCode, exitAddress);

    public static ConsumerOrderDownloadResult Error(string message, string? raw = null, HttpStatusCode? statusCode = null, string? exitAddress = null) =>
        new(ConsumerOrderDownloadStatus.Error, null, message, raw, statusCode, exitAddress);
}

