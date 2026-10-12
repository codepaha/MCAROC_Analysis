using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;
using MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

namespace MCAROC_Analysis.Services.LitigationModule.Orders;

/// <summary>Constructs stable per-source order keys (plan section 4.5).</summary>
public static class OrderKeyBuilder
{
    /// <summary>Builds the BPR order key from (PdfUrl, OrderDate, OrderType).</summary>
    public static string ForBpr(string? pdfUrl, string? orderDate, string? orderType)
    {
        var u = pdfUrl?.Trim() ?? string.Empty;
        var d = orderDate?.Trim() ?? string.Empty;
        var t = orderType?.Trim() ?? string.Empty;
        return $"{u}|{d}|{t}";
    }

    /// <summary>Builds the BPR order key from (PdfUrl, DateOnly, OrderType).</summary>
    public static string ForBpr(string? pdfUrl, DateOnly? orderDate, string? orderType) =>
        ForBpr(pdfUrl, orderDate?.ToString("yyyy-MM-dd"), orderType);

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

    /// <summary>Builds the consumer portal order key from (filingReferenceNumber, courtRoomHearingId, orderTypeId).</summary>
    public static string ForConsumer(long filingReferenceNumber, string? courtRoomHearingId, int? orderTypeId)
    {
        var h = courtRoomHearingId?.Trim() ?? "0";
        var t = orderTypeId?.ToString() ?? "0";
        return $"{filingReferenceNumber}|{h}|{t}";
    }

    /// <summary>Extracts or builds the order key from an <see cref="OrderSourceLocator"/> where applicable.</summary>
    public static string ForLocator(OrderSourceLocator locator)
    {
        if (locator.Source.Equals("Bpr", StringComparison.OrdinalIgnoreCase))
        {
            return ForBpr(locator.PdfUrl, locator.DateOfHearing, null);
        }

        if (locator.Source.Equals("Consumer", StringComparison.OrdinalIgnoreCase) && locator.FilingReferenceNumber is { } refNo)
        {
            return ForConsumer(refNo, locator.CourtRoomHearingId, locator.OrderTypeId);
        }

        if (locator.Source.Equals("Drt", StringComparison.OrdinalIgnoreCase))
        {
            return DrtOrderKeyHelper.ExtractOrderKey(locator.OrderUrl ?? locator.PdfUrl);
        }

        return (locator.OrderUrl ?? locator.PdfUrl ?? string.Empty).Trim();
    }
}

