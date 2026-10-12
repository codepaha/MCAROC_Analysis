using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>Constructs stable per-source order keys (plan section 4.5).</summary>
public static class OrderKeyBuilder
{
    /// <summary>Builds the BPR order key from (PdfUrl, OrderDate, OrderType).</summary>
    public static string ForBpr(string? pdfUrl, string? orderDate, string? orderType)
    {
        if (string.IsNullOrWhiteSpace(pdfUrl))
        {
            throw new ArgumentException("BPR order key requires a non-empty PdfUrl.", nameof(pdfUrl));
        }

        if (string.IsNullOrWhiteSpace(orderDate))
        {
            throw new ArgumentException("BPR order key requires a non-empty OrderDate.", nameof(orderDate));
        }

        if (string.IsNullOrWhiteSpace(orderType))
        {
            throw new ArgumentException("BPR order key requires a non-empty OrderType.", nameof(orderType));
        }

        return $"{pdfUrl.Trim()}|{orderDate.Trim()}|{orderType.Trim()}";
    }

    /// <summary>Builds the BPR order key from (PdfUrl, DateOnly, OrderType).</summary>
    public static string ForBpr(string? pdfUrl, DateOnly? orderDate, string? orderType)
    {
        if (!orderDate.HasValue)
        {
            throw new ArgumentException("BPR order key requires a valid OrderDate.", nameof(orderDate));
        }

        return ForBpr(pdfUrl, orderDate.Value.ToString("yyyy-MM-dd"), orderType);
    }

    /// <summary>Builds the DRT composite order key from (tribunalId, caseTypeId, caseNo, caseYear, kind, orderDate, url)
    /// using normalized file path (collapsing pdf1.php and qrpdfview.php) or SHA-256 of embedded HTML.</summary>
    public static string ForDrt(
        int tribunalId,
        int? caseTypeId,
        string? caseNo,
        int? caseYear,
        string kind,
        DateOnly? orderDate,
        string? url) =>
        DrtOrderKeyHelper.BuildCompositeOrderKey(tribunalId, caseTypeId, caseNo, caseYear, kind, orderDate, url);

    /// <summary>Builds the consumer portal order key from (filingReferenceNumber, courtRoomHearingId, orderTypeId).
    /// Both hearing ID and order type are required by the plan to identify an order and separate daily orders
    /// from judgments; incomplete identity is rejected rather than generating colliding keys.</summary>
    public static string ForConsumer(long filingReferenceNumber, string? courtRoomHearingId, int? orderTypeId)
    {
        if (filingReferenceNumber <= 0)
        {
            throw new ArgumentException("Consumer order key requires a positive filingReferenceNumber.", nameof(filingReferenceNumber));
        }

        if (string.IsNullOrWhiteSpace(courtRoomHearingId))
        {
            throw new ArgumentException("Consumer order key requires a non-empty courtRoomHearingId.", nameof(courtRoomHearingId));
        }

        if (!orderTypeId.HasValue)
        {
            throw new ArgumentException("Consumer order key requires a valid orderTypeId.", nameof(orderTypeId));
        }

        return $"{filingReferenceNumber}|{courtRoomHearingId.Trim()}|{orderTypeId.Value}";
    }

    /// <summary>Extracts or builds the order key from an <see cref="OrderSourceLocator"/> where applicable.</summary>
    public static string ForLocator(OrderSourceLocator locator)
    {
        if (locator.Source.Equals("Bpr", StringComparison.OrdinalIgnoreCase))
        {
            var dateStr = locator.OrderDate ?? locator.DateOfHearing?.ToString("yyyy-MM-dd");
            return ForBpr(locator.PdfUrl, dateStr, locator.OrderType);
        }

        if (locator.Source.Equals("Consumer", StringComparison.OrdinalIgnoreCase))
        {
            if (!locator.FilingReferenceNumber.HasValue)
            {
                throw new ArgumentException("Consumer order locator requires FilingReferenceNumber.", nameof(locator));
            }

            return ForConsumer(locator.FilingReferenceNumber.Value, locator.CourtRoomHearingId, locator.OrderTypeId);
        }

        if (locator.Source.Equals("Drt", StringComparison.OrdinalIgnoreCase))
        {
            return DrtOrderKeyHelper.ExtractOrderKey(locator.OrderUrl ?? locator.PdfUrl);
        }

        return (locator.OrderUrl ?? locator.PdfUrl ?? string.Empty).Trim();
    }
}

