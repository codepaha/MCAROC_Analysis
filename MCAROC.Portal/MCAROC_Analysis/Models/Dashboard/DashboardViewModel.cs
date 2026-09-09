using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

public record TrendPoint(DateOnly BucketStart, int Created);

public enum TrendGranularity
{
    Weekly,
    Monthly
}

public record SectionSeverityCount(FindingSection Section, FindingSeverity Severity, int Count);

public record TopRiskIndicatorRow(string Code, string Title, FindingSection Section, FindingSeverity MaxSeverity, int FindingCount, int CompaniesAffected);

public record PriorityRequestRow(
    McaRequest Request,
    ReviewPriority? Priority,
    int CriticalFindingsCount,
    AnalysisRunStatus? LatestRunStatus,
    List<string> AttentionReasons,
    string KeyFindingTitle);

public class DashboardViewModel
{
    public required DashboardFilterCriteria Filters { get; set; }
    public required List<Client> Clients { get; set; }
    public (DateOnly From, DateOnly To) Window { get; set; }

    // Operational KPI cards
    public int TotalRequests { get; set; }
    public double? TotalRequestsTrendPercent { get; set; }
    public int ProcessingCount { get; set; }
    public Dictionary<RequestStatus, int> ProcessingByRequestStatus { get; set; } = [];
    public Dictionary<FilingBatchStatus, int> ProcessingByFilingBatchStatus { get; set; } = [];
    public int AnalysisCompletedCount { get; set; }
    public double AnalysisCompletionRatePercent { get; set; }
    public int AttentionRequiredCount { get; set; }

    // Risk KPI cards
    public int HighPriorityRequestCount { get; set; }
    public int CriticalFindingsCount { get; set; }
    public int CriticalFindingsCompanyCount { get; set; }
    public int ReviewFindingsCount { get; set; }
    public int ReviewFindingsCompanyCount { get; set; }
    public int PositiveFindingsCount { get; set; }

    // Charts
    public List<TrendPoint> RequestTrend { get; set; } = [];
    public TrendGranularity TrendGranularity { get; set; }
    public Dictionary<ReviewPriority, int> PriorityDistribution { get; set; } = [];
    public List<SectionSeverityCount> FindingsBySection { get; set; } = [];

    // Tables
    public List<TopRiskIndicatorRow> TopRiskIndicators { get; set; } = [];
    public List<PriorityRequestRow> PriorityRequests { get; set; } = [];
}
