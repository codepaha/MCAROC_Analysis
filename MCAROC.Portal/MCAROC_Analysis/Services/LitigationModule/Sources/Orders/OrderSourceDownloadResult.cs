namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Standardized download outcome for any litigation order source adapter (plan section 4.5).</summary>
/// <param name="Ok">Whether the order was successfully retrieved and validated.</param>
/// <param name="Bytes">The raw binary bytes (e.g. PDF or binary attachment), or null if text/failure.</param>
/// <param name="ContentType">MIME content type (e.g. "application/pdf", "text/plain; charset=utf-8", "application/msword").</param>
/// <param name="Error">Error description or failure reason if not Ok.</param>
/// <param name="Text">Extracted or rendered plain text if served as HTML / text note.</param>
/// <param name="NotPublished">True if the court explicitly reports that no order has been published / available (e.g. HTTP 400 at Consumer portal or 404 at DRT).</param>
/// <param name="IsUnsupportedFormat">True if the source served an unsupported format in v1 (e.g. Word .doc, image, zip) that should be retained but marked unsupported for text extraction.</param>
/// <param name="ExitAddress">Proxy exit address or interface used for the request.</param>
public sealed record OrderSourceDownloadResult(
    bool Ok,
    byte[]? Bytes,
    string? ContentType,
    string? Error,
    string? Text = null,
    bool NotPublished = false,
    bool IsUnsupportedFormat = false,
    string? ExitAddress = null)
{
    public static OrderSourceDownloadResult Success(byte[] bytes, string contentType, string? exitAddress = null) =>
        new(true, bytes, contentType, null, null, false, false, exitAddress);

    public static OrderSourceDownloadResult OrderText(string text, string? exitAddress = null) =>
        new(true, null, "text/plain; charset=utf-8", null, text, false, false, exitAddress);

    public static OrderSourceDownloadResult NotPublishedAtSource(string reason, string? exitAddress = null) =>
        new(false, null, null, reason, null, NotPublished: true, false, exitAddress);

    public static OrderSourceDownloadResult Unsupported(byte[] bytes, string contentType, string reason, string? exitAddress = null) =>
        new(true, bytes, contentType, reason, null, false, IsUnsupportedFormat: true, exitAddress);

    public static OrderSourceDownloadResult Failed(string error, string? exitAddress = null) =>
        new(false, null, null, error, null, false, false, exitAddress);
}

