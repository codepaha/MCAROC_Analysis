namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Individual hearing entry from e-Jagriti case status history.</summary>
/// <param name="CourtRoomHearingId">Unique hearing ID assigned by e-Jagriti court room service.</param>
/// <param name="DateOfHearing">Calendar date of the hearing (IST).</param>
/// <param name="DateOfNextHearing">Calendar date scheduled for the next hearing, if any (IST).</param>
/// <param name="CaseStage">Stage recorded at this hearing (e.g. "REGISTERED", "ADJOURN", "ALLOWED", "DISMISSED").</param>
/// <param name="ProceedingText">Proceedings summary or notes (may contain HTML markup).</param>
/// <param name="DailyOrderAvailable">True if a daily order or judgment document was uploaded for this hearing.</param>
/// <param name="OrderTypeId">Order type ID (typically 1 for daily order/judgment).</param>
/// <param name="RawJson">Raw JSON representation of the hearing row for diagnostics.</param>
public sealed record ConsumerHearing(
    string CourtRoomHearingId,
    DateOnly? DateOfHearing,
    DateOnly? DateOfNextHearing,
    string CaseStage,
    string ProceedingText,
    bool DailyOrderAvailable,
    int? OrderTypeId = null,
    string? RawJson = null);

