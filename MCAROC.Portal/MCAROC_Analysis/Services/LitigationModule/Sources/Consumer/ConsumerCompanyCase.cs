using MCAROC_Analysis.Services.LitigationModule.Normalisation;

namespace MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;

/// <summary>Case summary record returned by e-Jagriti Cause Title report endpoint (getCauseTitleListByCompany).
/// Exposes the 15 portal fields plus enriched commission hierarchy context.</summary>
public sealed record ConsumerCompanyCase(
    string CaseNumber,
    string? FileApplicationNumber,
    string? CaseStageName,
    DateOnly? CaseFilingDate,
    DateOnly? DateOfHearing,
    DateOnly? DateOfNextHearing,
    DateOnly? DateOfDisposal,
    int? HearingSequenceNumber,
    string? ComplainantName,
    string? ComplainantAdvocateName,
    string? RespondentName,
    string? RespondentAdvocateName,
    string? CaseTypeName,
    string? CaseCategoryName,
    string? CommissionName,
    int CommissionId,
    int CommissionTypeId,
    string? StateName,
    string? DistrictName,
    DateOnly? EffectiveHearingDate,
    CaseNumberParts NormalizedCaseNumber,
    string? RawJson = null);

/// <summary>Result container for company / cause title search queries across commissions.</summary>
public sealed record ConsumerCompanySearchResult(
    IReadOnlyList<ConsumerCompanyCase> Cases,
    bool IsSuccess,
    string? ErrorMessage = null,
    string? ExitAddress = null,
    int RequestsMade = 1);

