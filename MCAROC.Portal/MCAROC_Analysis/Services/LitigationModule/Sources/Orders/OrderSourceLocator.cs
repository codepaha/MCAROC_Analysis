namespace MCAROC_Analysis.Services.LitigationModule.Sources.Orders;

/// <summary>Encapsulates the locator details of an order to download across supported sources (plan section 4.5).</summary>
/// <param name="Source">The source identifier: "Bpr", "Drt", "Consumer", etc.</param>
/// <param name="PdfUrl">The direct URL (used by BPR or direct DRT links).</param>
/// <param name="OrderUrl">Alternative order link (e.g. DRT pdf1.php or qrpdfview.php).</param>
/// <param name="FilingReferenceNumber">Filing reference number for consumer portal orders.</param>
/// <param name="DateOfHearing">Date of hearing for consumer portal orders.</param>
/// <param name="OrderTypeId">Order type ID (e.g. 1 for daily order, 2 for judgment).</param>
/// <param name="CourtRoomHearingId">Hearing room ID string if available.</param>
/// <param name="SourceLocatorJson">Serialized source locator JSON if available.</param>
public sealed record OrderSourceLocator(
    string Source,
    string? PdfUrl = null,
    string? OrderUrl = null,
    long? FilingReferenceNumber = null,
    DateOnly? DateOfHearing = null,
    int? OrderTypeId = null,
    string? CourtRoomHearingId = null,
    string? SourceLocatorJson = null);

