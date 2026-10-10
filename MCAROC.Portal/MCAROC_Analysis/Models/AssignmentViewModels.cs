using MCAROC_Analysis.Data.Entities;
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
    public IReadOnlyList<LegalCaseRecord> Cases { get; init; } = [];

    // KPI Metrics
    public int TotalCases => Cases.Count;
    public int HighCourtCases => Cases.Count(c => c.Category.Contains("high") || c.Court.Contains("high", StringComparison.OrdinalIgnoreCase));
    public int DistrictCases => Cases.Count(c => c.Category.Contains("district") || c.Court.Contains("district", StringComparison.OrdinalIgnoreCase));
    public int TribunalCases => Cases.Count(c => c.Category.Contains("drt") || c.Category.Contains("nclt") || c.Category.Contains("consumer") || c.Category.Contains("rera") || c.Category.Contains("tribunal"));
    public int IncludedCases => Cases.Count(c => !c.IsExcludedFromReport);
    public int ExcludedCases => Cases.Count(c => c.IsExcludedFromReport);
}

