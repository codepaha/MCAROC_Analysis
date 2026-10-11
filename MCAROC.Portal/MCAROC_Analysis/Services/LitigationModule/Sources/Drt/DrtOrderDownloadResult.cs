namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Result of downloading an order from DRT.</summary>
/// <param name="IsSuccess">True if a document was successfully downloaded.</param>
/// <param name="Bytes">The downloaded (and cleaned) document bytes.</param>
/// <param name="ContentType">MIME type (e.g. <c>application/pdf</c>, <c>text/html</c>).</param>
/// <param name="Format">Detected format (<c>pdf</c>, <c>html</c>, <c>doc</c>, <c>docx</c>, <c>png</c>, <c>jpg</c>, <c>zip</c>, or <c>unsupported</c>).</param>
/// <param name="ErrorMessage">Error message on failure.</param>
/// <param name="NotPublished">True if the order is confirmed not published or missing (e.g. 404/400).</param>
/// <param name="CleanedPdf">True if leading whitespace or stray prefix before %PDF was cleaned.</param>
public sealed record DrtOrderDownloadResult(
    bool IsSuccess,
    byte[]? Bytes,
    string? ContentType,
    string? Format,
    string? ErrorMessage,
    bool NotPublished,
    bool CleanedPdf)
{
    public static DrtOrderDownloadResult Success(byte[] bytes, string contentType, string format, bool cleanedPdf = false) =>
        new(true, bytes, contentType, format, null, false, cleanedPdf);

    public static DrtOrderDownloadResult NotAvailable(string message = "Order not available") =>
        new(false, null, null, null, message, true, false);

    public static DrtOrderDownloadResult Failure(string error) =>
        new(false, null, null, null, error, false, false);
}

