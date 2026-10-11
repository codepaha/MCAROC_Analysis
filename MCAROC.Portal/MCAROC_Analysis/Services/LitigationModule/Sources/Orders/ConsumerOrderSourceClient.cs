using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Order source adapter for Consumer Commission (e-Jagriti) orders (plan section 4.5).
/// Downloads through IConsumerClient (getDailyOrderJudgementPdf), handles base64 PDF and HTML notes,
/// and maps HTTP 400 "not available" to NotPublished.</summary>
public sealed partial class ConsumerOrderSourceClient : IOrderSourceClient
{
    private readonly IConsumerClient _client;

    public ConsumerOrderSourceClient(IConsumerClient client)
    {
        _client = client;
    }

    public bool CanHandle(OrderSourceLocator locator)
    {
        if (string.Equals(locator.Source, "Consumer", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "eJagriti", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "NCDRC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "SCDRC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "DCDRC", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return locator.FilingReferenceNumber.HasValue && locator.DateOfHearing.HasValue;
    }

    public async Task<OrderSourceDownloadResult> DownloadAsync(
        OrderSourceLocator locator,
        long maxBytes = 20 * 1024 * 1024,
        CancellationToken ct = default)
    {
        if (!locator.FilingReferenceNumber.HasValue || !locator.DateOfHearing.HasValue)
        {
            return OrderSourceDownloadResult.Failed("Consumer orders require FilingReferenceNumber and DateOfHearing");
        }

        var filingRef = locator.FilingReferenceNumber.Value;
        var hearingDate = locator.DateOfHearing.Value;
        var orderTypeId = locator.OrderTypeId ?? 1;

        var result = await _client.GetDailyOrderPdfAsync(filingRef, hearingDate, orderTypeId, ct);

        if (result.IsNotFound)
        {
            return OrderSourceDownloadResult.NotPublishedAtSource(
                result.Message ?? "Daily Order not available at Consumer Commission portal",
                result.ExitAddress ?? _client.CurrentExit);
        }

        if (result.IsUnavailable)
        {
            return OrderSourceDownloadResult.Failed(
                result.Message ?? "Consumer portal is unavailable",
                result.ExitAddress ?? _client.CurrentExit);
        }

        if (!result.IsSuccess || result.Document is null)
        {
            return OrderSourceDownloadResult.Failed(
                result.Message ?? "Failed to download Consumer order",
                result.ExitAddress ?? _client.CurrentExit);
        }

        var doc = result.Document;

        if (doc.Bytes.LongLength > maxBytes)
        {
            return OrderSourceDownloadResult.Failed(
                $"Document size ({doc.Bytes.LongLength} bytes) exceeds maximum limit ({maxBytes} bytes)",
                result.ExitAddress ?? _client.CurrentExit);
        }

        if (string.Equals(doc.ContentType, "html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(doc.Bytes);
            var plainText = ExtractHtmlText(html);
            return OrderSourceDownloadResult.OrderText(plainText, result.ExitAddress ?? _client.CurrentExit);
        }

        return OrderSourceDownloadResult.Success(
            doc.Bytes,
            doc.MimeType,
            result.ExitAddress ?? _client.CurrentExit);
    }

    private static string ExtractHtmlText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var text = ScriptStyleRegex().Replace(html, " ");
        text = BreakRegex().Replace(text, "\n");
        text = ParagraphRegex().Replace(text, "\n\n");
        text = TagRegex().Replace(text, " ");
        text = HttpUtility.HtmlDecode(text);
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

