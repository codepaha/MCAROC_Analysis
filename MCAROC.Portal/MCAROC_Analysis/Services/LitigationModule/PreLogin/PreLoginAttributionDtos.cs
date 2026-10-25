using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public sealed record PreLoginCaseAttributionDto(
    long ScopeCaseId,
    string CaseRowId,
    long? TargetId,
    string? TargetName,
    PreLoginTargetKind TargetKind,
    LitigationAttributionOutcome? Outcome,
    string? Rule,
    string? Party,
    string? Reason,
    bool IsExcluded,
    string? ExclusionReason,
    bool IsSuggestedExclusion,
    bool IsAutoExcluded,
    bool IsUnconfirmed = false,
    PersonAttributionGrade? Grade = null,
    string? SignalsJson = null
)
{
    public bool IsVerified => !IsExcluded && (Grade == PersonAttributionGrade.Confirmed || Grade == PersonAttributionGrade.Probable || (Grade == null && Outcome == LitigationAttributionOutcome.Attributed));
    public bool IsPossible => !IsExcluded && (Grade == PersonAttributionGrade.Possible || (Grade == null && (Outcome == LitigationAttributionOutcome.Ambiguous || IsUnconfirmed)));
};

public sealed record PreLoginSuggestionItemDto(
    long ScopeCaseId,
    long JobId,
    string BorrowerName,
    string Cin,
    Guid BatchId,
    string CaseRowId,
    long? TargetId,
    string TargetName,
    PreLoginTargetKind TargetKind,
    string? Court,
    string? CaseNo,
    string? Category,
    string Rule,
    string? Party,
    string Reason,
    DateTime SuggestedUtc
);

public sealed record PreLoginAutoExcludedItemDto(
    long ScopeCaseId,
    long JobId,
    string BorrowerName,
    string Cin,
    Guid BatchId,
    string CaseRowId,
    long? TargetId,
    string TargetName,
    PreLoginTargetKind TargetKind,
    string? Court,
    string? CaseNo,
    string? Category,
    string Rule,
    string? Party,
    string Reason,
    string ExclusionReason,
    DateTime ExcludedUtc
);

