using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.PreLogin;
using MCAROC_Analysis.Services.PreLoginReports;

namespace MCAROC_Analysis.Models;

public sealed class AssignmentListingItemViewModel
{
    public long PreLoginReportJobId { get; init; }
    public Guid BatchId { get; init; }
    public string Identifier { get; init; } = string.Empty;
    public string BorrowerName { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;
    public string BranchCode { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ProgressPercent { get; init; }
    public int TotalCases { get; init; }
    public bool NoLitigationFound { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? ReportStoragePath { get; init; }
}

public sealed class AssignmentListingViewModel
{
    public IReadOnlyList<AssignmentListingItemViewModel> Items { get; init; } = [];
    public string? SelectedStatus { get; init; }
    public string? SearchText { get; init; }
}

public sealed class AssignmentDetailViewModel
{
    public long PreLoginReportJobId { get; init; }
    public Guid BatchId { get; init; }
    public string Cin { get; init; } = string.Empty;
    public string BorrowerName { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public int ProgressPercent { get; init; }
    public int DataVersion { get; init; }
    public string? FailureReason { get; init; }
    public DateTime CreatedUtc { get; init; }
    public string? ReportStoragePath { get; init; }

    public BorrowerAssignmentDetails? Assignment { get; init; }
    public InstaReportData? StoredData { get; init; }
    public PreLoginLitigationTarget? BorrowerTarget { get; init; }
    public IReadOnlyList<PreLoginLitigationTarget> DirectorTargets { get; init; } = [];
    public IReadOnlyList<LegalCaseRecord> Cases { get; init; } = [];
    public IReadOnlyDictionary<string, PreLoginCaseAttributionDto> CaseAttributions { get; init; } = new Dictionary<string, PreLoginCaseAttributionDto>(StringComparer.OrdinalIgnoreCase);

    // KPI Metrics
    public int TotalCases => Cases.Count;
    public int HighCourtCases => Cases.Count(c => c.Category.Contains("high") || c.Court.Contains("high", StringComparison.OrdinalIgnoreCase));
    public int DistrictCases => Cases.Count(c => c.Category.Contains("district") || c.Court.Contains("district", StringComparison.OrdinalIgnoreCase));
    public int TribunalCases => Cases.Count(c => c.Category.Contains("drt") || c.Category.Contains("nclt") || c.Category.Contains("consumer") || c.Category.Contains("rera") || c.Category.Contains("tribunal"));
    public int IncludedCases => Cases.Count(c => !c.IsExcludedFromReport);
    public int ExcludedCases => Cases.Count(c => c.IsExcludedFromReport);
    public int SuggestedExclusionsCount => CaseAttributions.Values.Count(a => a.IsSuggestedExclusion);
    public int AutoExcludedCount => CaseAttributions.Values.Count(a => a.IsAutoExcluded);

    // #452 Attribution grade and verified metrics
    public int VerifiedCasesCount => CaseAttributions.Values.Count(a => !a.IsExcluded && (a.Grade == PersonAttributionGrade.Confirmed || a.Grade == PersonAttributionGrade.Probable || (a.Grade == null && a.Outcome == LitigationAttributionOutcome.Attributed)));
    public int PossibleCasesCount => CaseAttributions.Values.Count(a => !a.IsExcluded && (a.Grade == PersonAttributionGrade.Possible || a.IsUnconfirmed));
    public bool IsUnresolved => TotalCases > 0 && VerifiedCasesCount == 0;
}

public sealed class AssignmentSuggestionsViewModel
{
    public IReadOnlyList<PreLoginSuggestionItemDto> Items { get; init; } = [];
    public long? SelectedJobId { get; init; }
    public string? SelectedReasonKind { get; init; }
    public PreLoginTargetKind? SelectedTargetKind { get; init; }
    public IReadOnlyList<string> AvailableReasons { get; init; } = [];
}

public sealed class AssignmentAutoExclusionsViewModel
{
    public IReadOnlyList<PreLoginAutoExcludedItemDto> Items { get; init; } = [];
    public long? SelectedJobId { get; init; }
    public string? SelectedReasonKind { get; init; }
    public PreLoginTargetKind? SelectedTargetKind { get; init; }
    public IReadOnlyList<string> AvailableReasons { get; init; } = [];
}

public sealed class AssignmentTrustReportViewModel
{
    public PreLoginTrustReport Report { get; init; } = null!;
    public long? SelectedJobId { get; init; }
    public DateTime? FromDate { get; init; }
    public DateTime? ToDate { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}

