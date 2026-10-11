using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Comprehensive case detail returned by <c>getCaseDetailCaseNoWise</c>, <c>getCaseDetailDiaryNoWise</c>,
/// or <c>getDratCaseDetailCaseNoWise</c> (26 fields + hearings).</summary>
public sealed record DrtCaseDetails(
    int TribunalId,
    int? CaseTypeId,
    string? CaseType,
    string? CaseTypeCode,
    string? CaseNo,
    int? CaseYear,
    CaseNumberParts CaseNumber,
    string? DiaryNo,
    int? DiaryYear,
    string? FilingNo,
    DateOnly? DateOfFiling,
    string? Status,
    CaseStatusParts StatusParts,
    string? StatusCode,
    DateOnly? DateOfDisposal,
    string? DisposalNature,
    DateOnly? NextListingDate,
    string? NextListingPurpose,
    string? CourtNo,
    string? CourtName,
    string? MainCaseCaseNo,
    decimal? SuitAmount,
    string? PetitionerName,
    string? RespondentName,
    string? PetitionerAddress,
    string? RespondentAddress,
    string? PetitionerAdvocate,
    string? RespondentAdvocate,
    string? AdditionalPetitioner,
    string? AdditionalRespondent,
    IReadOnlyList<DrtHearing> Hearings,
    string? IaDetails);

