using MCAROC_Analysis.Services.LitigationData;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Order source adapter for BPR vendor orders (plan section 4.5).
/// Wraps existing BprLitigationClient so downstream consumers use unified IOrderSourceClient.</summary>
public sealed class BprOrderSourceClient : IOrderSourceClient
{
    private readonly BprLitigationClient _client;
    private readonly IOptions<BprLitigationOptions>? _options;

    public BprOrderSourceClient(BprLitigationClient client, IOptions<BprLitigationOptions>? options = null)
    {
        _client = client;
        _options = options;
    }

    public bool CanHandle(OrderSourceLocator locator)
    {
        if (string.Equals(locator.Source, "Bpr", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(locator.Source, "Vendor", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Default fallback if a direct PdfUrl is supplied without specific court domain
        return !string.IsNullOrWhiteSpace(locator.PdfUrl) &&
               string.IsNullOrWhiteSpace(locator.OrderUrl) &&
               !locator.FilingReferenceNumber.HasValue;
    }

    public async Task<OrderSourceDownloadResult> DownloadAsync(
        OrderSourceLocator locator,
        long maxBytes = 20 * 1024 * 1024,
        CancellationToken ct = default)
    {
        var url = locator.PdfUrl ?? locator.OrderUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return OrderSourceDownloadResult.Failed("No PDF URL provided for BPR order");
        }

        try
        {
            var token = await _client.AuthenticateAsync(ct);
            var limit = _options?.Value?.MaxOrderPdfBytes ?? maxBytes;
            var res = await _client.DownloadOrderDocumentAsync(token, url, limit, ct);

            if (res.NotPublished)
            {
                return OrderSourceDownloadResult.NotPublishedAtSource(res.Error ?? "Order not published at source");
            }

            if (!res.Ok)
            {
                return OrderSourceDownloadResult.Failed(res.Error ?? "BPR order download failed");
            }

            if (res.Text is not null)
            {
                return OrderSourceDownloadResult.OrderText(res.Text);
            }

            return OrderSourceDownloadResult.Success(res.Bytes ?? [], res.ContentType ?? "application/pdf");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OrderSourceDownloadResult.Failed($"BPR download error: {ex.Message}");
        }
    }
}

