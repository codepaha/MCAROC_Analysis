using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Order source adapter for DRT/DRAT court orders (plan section 4.5).
/// Downloads through IDrtClient, handles URL detection (pdf1.php, qrpdfview.php, embedded HTML in file= parameter),
/// cleans PDF whitespace and prefixes via DrtOrderCleaner, and identifies unsupported formats (Word, image, zip).</summary>
public sealed partial class DrtOrderSourceClient : IOrderSourceClient
{
    private readonly IDrtClient _client;

    public DrtOrderSourceClient(IDrtClient client)
    {
        _client = client;
    }

    public bool CanHandle(OrderSourceLocator locator)
    {
        if (string.Equals(locator.Source, "Drt", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "DRAT", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var url = locator.OrderUrl ?? locator.PdfUrl;
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url.Contains("drt.gov.in", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("pdf1.php", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("qrpdfview.php", StringComparison.OrdinalIgnoreCase) ||
                   url.Contains("pdf_html.php", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public async Task<OrderSourceDownloadResult> DownloadAsync(
        OrderSourceLocator locator,
        long maxBytes = 20 * 1024 * 1024,
        CancellationToken ct = default)
    {
        var url = locator.OrderUrl ?? locator.PdfUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return OrderSourceDownloadResult.Failed("No URL provided in DRT order locator");
        }

        // Special case: check if the HTML content is embedded directly in the URL's "file=" parameter
        var embeddedHtml = TryExtractEmbeddedHtmlFromUrl(url);
        if (embeddedHtml is not null)
        {
            var plainText = ExtractHtmlText(embeddedHtml);
            return OrderSourceDownloadResult.OrderText(plainText, _client.CurrentExit);
        }

        var drtResult = await _client.DownloadOrderAsync(url, maxBytes, ct);

        if (drtResult.NotPublished)
        {
            return OrderSourceDownloadResult.NotPublishedAtSource(drtResult.ErrorMessage ?? "Order not published at DRT portal", _client.CurrentExit);
        }

        if (!drtResult.IsSuccess)
        {
            return OrderSourceDownloadResult.Failed(drtResult.ErrorMessage ?? "DRT order download failed", _client.CurrentExit);
        }

        // Check unsupported formats in v1 (Word .doc, images, zip)
        if (string.Equals(drtResult.Format, "doc", StringComparison.OrdinalIgnoreCase))
        {
            return OrderSourceDownloadResult.Unsupported(
                drtResult.Bytes ?? [],
                drtResult.ContentType ?? "application/msword",
                "Word .doc format is not supported for text extraction in v1",
                _client.CurrentExit);
        }

        if (string.Equals(drtResult.Format, "png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(drtResult.Format, "jpg", StringComparison.OrdinalIgnoreCase))
        {
            return OrderSourceDownloadResult.Unsupported(
                drtResult.Bytes ?? [],
                drtResult.ContentType ?? "image/jpeg",
                "Image format is not supported for direct text extraction in v1 (requires OCR)",
                _client.CurrentExit);
        }

        if (string.Equals(drtResult.Format, "zip", StringComparison.OrdinalIgnoreCase))
        {
            return OrderSourceDownloadResult.Unsupported(
                drtResult.Bytes ?? [],
                drtResult.ContentType ?? "application/zip",
                "Zip archive format is not supported for text extraction in v1",
                _client.CurrentExit);
        }

        // HTML format served by portal (e.g. from pdf_html.php)
        if (string.Equals(drtResult.ContentType, "text/html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(drtResult.Format, "html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(drtResult.Bytes ?? []);
            var plainText = ExtractHtmlText(html);
            return OrderSourceDownloadResult.OrderText(plainText, _client.CurrentExit);
        }

        // Standard PDF
        return OrderSourceDownloadResult.Success(
            drtResult.Bytes ?? [],
            drtResult.ContentType ?? "application/pdf",
            _client.CurrentExit);
    }

    /// <summary>Checks whether the order URL carries embedded HTML content directly in its query parameters (e.g. file=&lt;html&gt;...).</summary>
    public static string? TryExtractEmbeddedHtmlFromUrl(string url)
    {
        var qIdx = url.IndexOf('?');
        if (qIdx < 0 || qIdx >= url.Length - 1) return null;

        var query = url[(qIdx + 1)..];
        var queryParams = HttpUtility.ParseQueryString(query);
        var fileParam = queryParams["file"];
        if (string.IsNullOrWhiteSpace(fileParam)) return null;

        var decoded = HttpUtility.UrlDecode(fileParam).Trim();
        if (decoded.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            decoded.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            (decoded.StartsWith("<") && (decoded.Contains("</p>") || decoded.Contains("</div>") || decoded.Contains("<br"))))
        {
            return decoded;
        }

        return null;
    }

    private static string ExtractHtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        // Strip scripts and styles
        var text = ScriptStyleRegex().Replace(html, " ");
        // Replace breaks and paragraphs with newlines
        text = BreakRegex().Replace(text, "\n");
        text = ParagraphRegex().Replace(text, "\n\n");
        // Strip remaining HTML tags
        text = TagRegex().Replace(text, " ");
        // Decode HTML entities
        text = HttpUtility.HtmlDecode(text);
        // Normalize whitespace
        var lines = text.Split('\n').Select(l => MultipleWhitespaceRegex().Replace(l.Trim(), " "));
        return string.Join("\n", lines.Where(l => l.Length > 0)).Trim();
    }

    [GeneratedRegex(@"<(script|style)[^>]*?>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"</p>|</div>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.IgnoreCase)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.None)]
    private static partial Regex MultipleWhitespaceRegex();
}

