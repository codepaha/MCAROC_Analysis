namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>A single hearing/proceeding entry from <c>caseProceedingDetails</c>.</summary>
/// <param name="CauseListDate">Hearing / causelist date, parsed day-first <c>d/M/yyyy</c>.</param>
/// <param name="Purpose">Hearing purpose (e.g. <c>Arguments</c>, <c>Compliance</c>, <c>Order</c>).</param>
/// <param name="CourtName">Presiding officer or bench name.</param>
/// <param name="CourtNo">Court room number if stated.</param>
/// <param name="AsCourtName">Assistant registrar / court name.</param>
/// <param name="OrderUrl">Direct order viewer/download URL if attached to this hearing.</param>
/// <param name="OrderKey">Normalised deduplication key for this order file per plan section 4.5.</param>
public sealed record DrtHearing(
    DateOnly? CauseListDate,
    string? Purpose,
    string? CourtName,
    string? CourtNo,
    string? AsCourtName,
    string? OrderUrl,
    string? OrderKey);

