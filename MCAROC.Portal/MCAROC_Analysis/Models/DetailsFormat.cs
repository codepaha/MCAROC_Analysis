using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>Small formatting helpers shared by the company-page (Details) tab partials. Razor partials
/// don't inherit the parent view's local <c>@functions</c>, so these live here instead of being copied
/// into every partial.</summary>
public static class DetailsFormat
{
    public static string Money(decimal? v) => v is null ? "—" : $"₹{v.Value:N2} Cr";
    public static string Num(decimal? v, int dp = 2) => v?.ToString("N" + dp) ?? "—";
    public static string D(DateOnly? d) => d?.ToString("d MMM yyyy") ?? "—";
    public static string Pct(decimal? v) => v is null ? "—" : $"{v.Value:0.##}%";
    public static string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s!;

    public static string StatusPill(RequestStatus s) => s switch
    {
        RequestStatus.AnalysisCompleted => "pi-status-completed",
        RequestStatus.ExtractionFailed or RequestStatus.AiAnalysisFailed or RequestStatus.ValidationFailed or RequestStatus.Cancelled => "pi-status-failed",
        RequestStatus.AiAnalysisInProgress or RequestStatus.ExtractionInProgress or RequestStatus.Validating => "pi-status-processing",
        _ => "pi-status-pending"
    };

    public static string PriorityPill(ReviewPriority p) => p switch
    {
        ReviewPriority.High => "pi-status-failed",
        ReviewPriority.Medium => "pi-status-processing",
        _ => "pi-status-completed"
    };

    public static string SevClass(FindingSeverity s) => s switch
    {
        FindingSeverity.Critical => "sev-critical",
        FindingSeverity.Review => "sev-review",
        FindingSeverity.Watch => "sev-watch",
        _ => "sev-positive"
    };

    public static string RoleLabel(LitigationRole r) => r switch
    {
        LitigationRole.FiledAgainst => "Filed against company",
        LitigationRole.FiledBy => "Filed by company",
        _ => "Role not determined"
    };
}
