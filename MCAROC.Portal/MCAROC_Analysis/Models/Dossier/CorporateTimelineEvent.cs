namespace MCAROC_Analysis.Models.Dossier;

/// <summary>The domain a <see cref="CorporateTimelineEvent"/> belongs to — drives the portal's category
/// filter chips. Deliberately does not include Litigation, related-party transactions, auditor changes,
/// or registered-office history — see <c>CorporateTimelineBuilder</c>'s class doc for why each is
/// excluded from v1.</summary>
public enum TimelineEventCategory { Corporate, Directors, Charges, Capital, CreditRatings, Gst, Epfo, Compliance, FinancialDisputes }

/// <summary>Where one <see cref="CorporateTimelineEvent"/> came from — always a single source row, drawn
/// from that row's own <see cref="Data.Entities.ExtractedEntityBase"/> fields. Used both for on-screen
/// traceability and as the same-day ordering tie-breaker (never incidental list-build order).</summary>
public sealed record TimelineEventProvenance(
    string EntityType,
    long EntityId,
    string? SourceSheetName,
    int? SourceRowNumber,
    long? SourceDocumentId);

/// <summary>One dated corporate event for the portal's Timeline tab. Deliberately NOT part of
/// <see cref="DossierModel"/> — see <c>CorporateTimelineBuilder</c>.</summary>
public sealed record CorporateTimelineEvent(
    DateOnly Date,
    TimelineEventCategory Category,
    string Title,
    string? Detail,
    TimelineEventProvenance Provenance);
