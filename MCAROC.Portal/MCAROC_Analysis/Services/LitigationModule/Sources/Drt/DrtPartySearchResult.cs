using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>A single party-search result row from <c>casedetail_party_name_wise</c> or <c>drat_party_name_wise</c>.</summary>
/// <param name="TribunalId">The tribunal schema ID searched.</param>
/// <param name="RawCaseType">Raw case type text as returned by the portal.</param>
/// <param name="RawCaseNo">Raw case number as returned by the portal (e.g. <c>MA/21/2016</c>).</param>
/// <param name="CaseNumber">Normalised case number parts from <see cref="CaseNumberNormaliser"/>.</param>
/// <param name="DiaryNo">Portal diary number (e.g. <c>9921/2017</c>).</param>
/// <param name="DateOfFiling">Filing date, parsed day-first <c>d/M/yyyy</c>.</param>
/// <param name="Applicant">Applicant / petitioner party name.</param>
/// <param name="Respondent">Respondent party name.</param>
/// <param name="PetitionerAdvocate">Applicant's advocate.</param>
/// <param name="RespondentAdvocate">Respondent's advocate.</param>
/// <param name="FilingNo">Portal internal filing number.</param>
public sealed record DrtPartySearchResult(
    int TribunalId,
    string? RawCaseType,
    string? RawCaseNo,
    CaseNumberParts CaseNumber,
    string? DiaryNo,
    DateOnly? DateOfFiling,
    string? Applicant,
    string? Respondent,
    string? PetitionerAdvocate,
    string? RespondentAdvocate,
    string? FilingNo);

