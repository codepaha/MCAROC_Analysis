using System.Text;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Cleans and classifies downloaded DRT order files (PDF whitespace stripping, stray prefixes, HTML detection).</summary>
public static class DrtOrderCleaner
{
    private static readonly byte[] PdfMagic = [0x25, 0x50, 0x44, 0x46]; // "%PDF"
    private static readonly byte[] DocMagic = [0xD0, 0xCF, 0x11, 0xE0];
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];
    private static readonly byte[] JpgMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04]; // "PK\x03\x04"

    /// <summary>Cleans raw downloaded bytes and determines format and content type.</summary>
    public static DrtOrderDownloadResult Clean(byte[] rawBytes, string? url = null)
    {
        if (rawBytes.Length == 0)
            return DrtOrderDownloadResult.Failure("Empty response body");

        // Strip leading whitespace / newlines
        var start = 0;
        while (start < rawBytes.Length && (rawBytes[start] == ' ' || rawBytes[start] == '\t' || rawBytes[start] == '\r' || rawBytes[start] == '\n' || rawBytes[start] == 0))
        {
            start++;
        }

        var trimmed = start == 0 ? rawBytes : rawBytes[start..];
        if (trimmed.Length == 0)
            return DrtOrderDownloadResult.Failure("Body contains only whitespace");

        var cleaned = start > 0;

        // 1. Direct %PDF
        if (StartsWith(trimmed, PdfMagic))
        {
            return DrtOrderDownloadResult.Success(trimmed, "application/pdf", "pdf", cleaned);
        }

        // 2. Stray prefix before %PDF (e.g. "<id>#1" within first 64 bytes)
        var limit = Math.Min(64, trimmed.Length - PdfMagic.Length);
        for (var i = 1; i <= limit; i++)
        {
            if (StartsWith(trimmed.AsSpan(i), PdfMagic))
            {
                var body = trimmed[i..];
                return DrtOrderDownloadResult.Success(body, "application/pdf", "pdf", true);
            }
        }

        // 3. HTML orders (e.g. from pdf_html.php or web error note)
        var inspectLen = Math.Min(500, trimmed.Length);
        var preview = Encoding.UTF8.GetString(trimmed, 0, inspectLen);
        var isHtmlUrl = url is not null && url.Contains("pdf_html.php", StringComparison.OrdinalIgnoreCase);

        if (isHtmlUrl || preview.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            preview.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            (preview.StartsWith("<") && preview.Contains("</")))
        {
            return DrtOrderDownloadResult.Success(trimmed, "text/html", "html", cleaned);
        }

        // 4. Word DOC
        if (StartsWith(trimmed, DocMagic))
            return DrtOrderDownloadResult.Success(trimmed, "application/msword", "doc", cleaned);

        // 5. Images (scanned orders uploaded directly)
        if (StartsWith(trimmed, PngMagic))
            return DrtOrderDownloadResult.Success(trimmed, "image/png", "png", cleaned);

        if (StartsWith(trimmed, JpgMagic))
            return DrtOrderDownloadResult.Success(trimmed, "image/jpeg", "jpg", cleaned);

        // 6. DOCX / ZIP
        if (StartsWith(trimmed, ZipMagic))
        {
            var zipPreviewLen = Math.Min(4000, trimmed.Length);
            var zipPreview = Encoding.ASCII.GetString(trimmed, 0, zipPreviewLen);
            if (zipPreview.Contains("word/", StringComparison.OrdinalIgnoreCase))
                return DrtOrderDownloadResult.Success(trimmed, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "docx", cleaned);

            return DrtOrderDownloadResult.Success(trimmed, "application/zip", "zip", cleaned);
        }

        // Unsupported format
        return new DrtOrderDownloadResult(
            IsSuccess: false,
            Bytes: trimmed,
            ContentType: "application/octet-stream",
            Format: "unsupported",
            ErrorMessage: $"Unsupported file signature (starts with: {preview[..Math.Min(20, preview.Length)]})",
            NotPublished: false,
            CleanedPdf: cleaned);
    }

    private static bool StartsWith(ReadOnlySpan<byte> span, byte[] prefix)
    {
        if (span.Length < prefix.Length) return false;
        return span[..prefix.Length].SequenceEqual(prefix);
    }
}

