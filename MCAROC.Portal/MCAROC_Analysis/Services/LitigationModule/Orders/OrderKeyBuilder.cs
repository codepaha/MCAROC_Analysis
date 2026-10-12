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
    /// using normalized file path (collapsing pdf1.php and qrpdfview.php) or SHA-256 of embedded HTML.
    /// Rejects incomplete identity fields before building the key to prevent key collisions across incomplete records.</summary>
    public static string ForDrt(
        int tribunalId,
        int? caseTypeId,
        string? caseNo,
        int? caseYear,
        string? kind,
        DateOnly? orderDate,
        string? url)
    {
        if (tribunalId <= 0)
        {
            throw new ArgumentException("DRT order key requires a positive tribunalId.", nameof(tribunalId));
        }

        if (!caseTypeId.HasValue || caseTypeId.Value <= 0)
        {
            throw new ArgumentException("DRT order key requires a valid caseTypeId.", nameof(caseTypeId));
        }

        if (string.IsNullOrWhiteSpace(caseNo))
        {
            throw new ArgumentException("DRT order key requires a non-empty caseNo.", nameof(caseNo));
        }

        if (!caseYear.HasValue || caseYear.Value <= 0)
        {
            throw new ArgumentException("DRT order key requires a valid caseYear.", nameof(caseYear));
        }

        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("DRT order key requires a non-empty kind (e.g. daily/final).", nameof(kind));
        }

        if (!orderDate.HasValue)
        {
            throw new ArgumentException("DRT order key requires a valid orderDate.", nameof(orderDate));
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("DRT order key requires a non-empty url.", nameof(url));
        }

        return DrtOrderKeyHelper.BuildCompositeOrderKey(tribunalId, caseTypeId.Value, caseNo, caseYear.Value, kind, orderDate.Value, url);
    }

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
            var url = locator.OrderUrl ?? locator.PdfUrl;
            var kind = locator.OrderKind ?? locator.OrderType;
            var date = locator.DateOfHearing ?? (DateOnly.TryParse(locator.OrderDate, out var d) ? d : (DateOnly?)null);

            if (!locator.TribunalId.HasValue)
            {
                throw new ArgumentException("DRT order locator requires TribunalId to build composite order key.", nameof(locator));
            }

            if (!locator.CaseTypeId.HasValue)
            {
                throw new ArgumentException("DRT order locator requires CaseTypeId to build composite order key.", nameof(locator));
            }

            if (string.IsNullOrWhiteSpace(locator.CaseNo))
            {
                throw new ArgumentException("DRT order locator requires CaseNo to build composite order key.", nameof(locator));
            }

            if (!locator.CaseYear.HasValue)
            {
                throw new ArgumentException("DRT order locator requires CaseYear to build composite order key.", nameof(locator));
            }

            if (string.IsNullOrWhiteSpace(kind))
            {
                throw new ArgumentException("DRT order locator requires OrderKind or OrderType to build composite order key.", nameof(locator));
            }

            if (!date.HasValue)
            {
                throw new ArgumentException("DRT order locator requires DateOfHearing or OrderDate to build composite order key.", nameof(locator));
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                throw new ArgumentException("DRT order locator requires OrderUrl or PdfUrl.", nameof(locator));
            }

            return ForDrt(
                locator.TribunalId.Value,
                locator.CaseTypeId.Value,
                locator.CaseNo,
                locator.CaseYear.Value,
                kind,
                date.Value,
                url);
        }

        return (locator.OrderUrl ?? locator.PdfUrl ?? string.Empty).Trim();
    }

    /// <summary>Extracts only the normalised DRT order file path key from a URL (e.g. from <c>file=</c> param).
    /// Kept separate from the planned composite order identity key.</summary>
    public static string ForDrtFilePath(string? url) => DrtOrderKeyHelper.ExtractOrderKey(url);
}

