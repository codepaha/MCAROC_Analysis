namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Order or judgment metadata returned by daily / final order reports.</summary>
/// <param name="DiaryNo">Diary number associated with the order.</param>
/// <param name="DateOfFiling">Filing date, parsed day-first <c>d/M/yyyy</c>.</param>
/// <param name="PronouncedDate">Date the order/judgment was pronounced, parsed day-first <c>d/M/yyyy</c>.</param>
/// <param name="Applicant">Applicant name.</param>
/// <param name="Respondent">Respondent name.</param>
/// <param name="PronouncedBy">Presiding officer who pronounced the order.</param>
/// <param name="OrderUrl">Direct URL to the order PDF or viewer.</param>
/// <param name="OrderType"><c>daily</c> or <c>final</c>.</param>
/// <param name="ItemNo">Cause list item number.</param>
/// <param name="OrderKey">Deduplication key per plan section 4.5.</param>
public sealed record DrtOrder(
    string? DiaryNo,
    DateOnly? DateOfFiling,
    DateOnly? PronouncedDate,
    string? Applicant,
    string? Respondent,
    string? PronouncedBy,
    string? OrderUrl,
    string OrderType,
    string? ItemNo,
    string? OrderKey);

