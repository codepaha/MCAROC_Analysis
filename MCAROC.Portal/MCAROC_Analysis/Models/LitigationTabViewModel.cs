using System.Globalization;
using System.Linq.Expressions;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Models;

public enum LitigationCaseStatusBucket
{
    Pending,
    Disposed,
    Unknown
}

/// <summary>
/// Centralized classifier and canonical EF Core SQL expressions for litigation case status and stage.
/// ASCII Casing Contract:
/// - All legal status tokens are 7-bit ASCII strings ("dispos", "pend", etc.).
/// - EF Core queries use string.ToLower() which reliably translates to LOWER([column]) in SQL Server.
/// - In-memory classification uses culture-invariant lowercasing (ToLowerInvariant()) over the same ASCII token set.
/// - Precedence rule: Disposed tokens have priority over Pending tokens (e.g. "disposed after hearing" -> Disposed).
/// - Mutual exclusivity: Every case maps to exactly one of Pending, Disposed, or Unknown.
/// </summary>
public static class LitigationCaseStatusClassifier
{
    public static readonly Expression<Func<LitigationCase, bool>> IsDisposedExpr = c =>
        (c.CaseStatus != null && (
            c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
            c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
            c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
            c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
            c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject")
        )) || (c.CaseStage != null && (
            c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
            c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
            c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
            c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
            c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject")
        ));

    public static readonly Expression<Func<LitigationCase, bool>> IsPendingExpr = c =>
        !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject")
            ))
        ) && (
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("pend")  || c.CaseStatus.ToLower().Contains("admit") ||
                c.CaseStatus.ToLower().Contains("hear")  || c.CaseStatus.ToLower().Contains("stage") ||
                c.CaseStatus.ToLower().Contains("evid")  || c.CaseStatus.ToLower().Contains("argum") ||
                c.CaseStatus.ToLower().Contains("notic") || c.CaseStatus.ToLower().Contains("stay") ||
                c.CaseStatus.ToLower().Contains("trial") || c.CaseStatus.ToLower().Contains("appear")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("pend")  || c.CaseStage.ToLower().Contains("admit") ||
                c.CaseStage.ToLower().Contains("hear")  || c.CaseStage.ToLower().Contains("stage") ||
                c.CaseStage.ToLower().Contains("evid")  || c.CaseStage.ToLower().Contains("argum") ||
                c.CaseStage.ToLower().Contains("notic") || c.CaseStage.ToLower().Contains("stay") ||
                c.CaseStage.ToLower().Contains("trial") || c.CaseStage.ToLower().Contains("appear")
            ))
        );

    public static readonly Expression<Func<LitigationCase, bool>> IsUnknownExpr = c =>
        !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("dispos") || c.CaseStatus.ToLower().Contains("clos") ||
                c.CaseStatus.ToLower().Contains("dismis") || c.CaseStatus.ToLower().Contains("withdr") ||
                c.CaseStatus.ToLower().Contains("settl")  || c.CaseStatus.ToLower().Contains("decid") ||
                c.CaseStatus.ToLower().Contains("quash")  || c.CaseStatus.ToLower().Contains("decree") ||
                c.CaseStatus.ToLower().Contains("allow")  || c.CaseStatus.ToLower().Contains("reject")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("dispos") || c.CaseStage.ToLower().Contains("clos") ||
                c.CaseStage.ToLower().Contains("dismis") || c.CaseStage.ToLower().Contains("withdr") ||
                c.CaseStage.ToLower().Contains("settl")  || c.CaseStage.ToLower().Contains("decid") ||
                c.CaseStage.ToLower().Contains("quash")  || c.CaseStage.ToLower().Contains("decree") ||
                c.CaseStage.ToLower().Contains("allow")  || c.CaseStage.ToLower().Contains("reject")
            ))
        ) && !(
            (c.CaseStatus != null && (
                c.CaseStatus.ToLower().Contains("pend")  || c.CaseStatus.ToLower().Contains("admit") ||
                c.CaseStatus.ToLower().Contains("hear")  || c.CaseStatus.ToLower().Contains("stage") ||
                c.CaseStatus.ToLower().Contains("evid")  || c.CaseStatus.ToLower().Contains("argum") ||
                c.CaseStatus.ToLower().Contains("notic") || c.CaseStatus.ToLower().Contains("stay") ||
                c.CaseStatus.ToLower().Contains("trial") || c.CaseStatus.ToLower().Contains("appear")
            )) || (c.CaseStage != null && (
                c.CaseStage.ToLower().Contains("pend")  || c.CaseStage.ToLower().Contains("admit") ||
                c.CaseStage.ToLower().Contains("hear")  || c.CaseStage.ToLower().Contains("stage") ||
                c.CaseStage.ToLower().Contains("evid")  || c.CaseStage.ToLower().Contains("argum") ||
                c.CaseStage.ToLower().Contains("notic") || c.CaseStage.ToLower().Contains("stay") ||
                c.CaseStage.ToLower().Contains("trial") || c.CaseStage.ToLower().Contains("appear")
            ))
        );

    private static readonly string[] DisposedTokens =
    [
        "dispos", "clos", "dismis", "withdr", "settl", "decid", "quash", "decree", "allow", "reject"
    ];

    private static readonly string[] PendingTokens =
    [
        "pend", "admit", "hear", "stage", "evid", "argum", "notic", "stay", "trial", "appear"
    ];

    public static LitigationCaseStatusBucket Classify(LitigationCase c) =>
        Classify(c.CaseStatus, c.CaseStage);

    public static LitigationCaseStatusBucket Classify(string? caseStatus, string? caseStage = null)
    {
        var status = caseStatus?.ToLowerInvariant();
        var stage = caseStage?.ToLowerInvariant();

        if (IsDisposedMatch(status, stage)) return LitigationCaseStatusBucket.Disposed;
        if (IsPendingMatch(status, stage)) return LitigationCaseStatusBucket.Pending;
        return LitigationCaseStatusBucket.Unknown;
    }

    private static bool IsDisposedMatch(string? status, string? stage)
    {
        foreach (var t in DisposedTokens)
        {
            if (status != null && status.Contains(t, StringComparison.Ordinal)) return true;
            if (stage != null && stage.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static bool IsPendingMatch(string? status, string? stage)
    {
        if (IsDisposedMatch(status, stage)) return false;
        foreach (var t in PendingTokens)
        {
            if (status != null && status.Contains(t, StringComparison.Ordinal)) return true;
            if (stage != null && stage.Contains(t, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

public enum SnapshotImportState
{
    NotCreated,
    AwaitingSnapshotCreation,
    InProgress,
    Completed,
    Failed,
    SnapshotMissing
}

public sealed class LitigationTabViewModel
{
    public required McaRequest Request { get; set; }
    public LitigationSearchJob? SearchJob { get; set; }
    public LitigationReportSnapshot? AuthoritativeSnapshot { get; set; }
    public LitigationReportSnapshot? CurrentAttemptSnapshot { get; set; }
    public SnapshotImportState ImportState { get; set; } = SnapshotImportState.NotCreated;

    public bool IsPriorRunDataShown { get; set; }
    public bool IsCurrentAttemptFailed =>
        SearchJob is { Status: LitigationSearchJobStatus.Failed } ||
        CurrentAttemptSnapshot is { Status: LitigationReportSnapshotStatus.Failed } ||
        ImportState == SnapshotImportState.SnapshotMissing;

    public LitigationCourtSummaryGrid CourtSummaryGrid { get; set; } = new();
    public LitigationSourceCoverageViewModel SourceCoverage { get; set; } = new();

    public List<LitigationCaseCardViewModel> Cases { get; set; } = [];
    public int TotalCaseCount { get; set; }
    public int CurrentPage { get; set; } = 1;
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCaseCount / PageSize) : 0;
    public int PageSize { get; set; } = 25;

    public LitigationPortfolioAiAnalysisViewModel? PortfolioAnalysis { get; set; }
    public bool IsReviewer { get; set; }
}

public sealed class LitigationCourtSummaryGrid
{
    public List<LitigationCourtSummaryRow> Rows { get; set; } = [];

    public int TotalCases => Rows.Sum(r => r.TotalCases);
    public int TotalPendingCases => Rows.Sum(r => r.PendingCases);
    public int TotalDisposedCases => Rows.Sum(r => r.DisposedCases);
    public int TotalUnknownCases => Rows.Sum(r => r.UnknownCases);
    public int TotalOrders => Rows.Sum(r => r.TotalOrders);

    public bool IsReconciled => TotalCases == TotalPendingCases + TotalDisposedCases + TotalUnknownCases;
}

public sealed class LitigationCourtSummaryRow
{
    public string CourtName { get; set; } = string.Empty;
    public string? CourtCategory { get; set; }
    public int TotalCases { get; set; }
    public int PendingCases { get; set; }
    public int DisposedCases { get; set; }
    public int UnknownCases { get; set; }
    public int TotalOrders { get; set; }
}

public sealed class LitigationSourceCoverageViewModel
{
    public List<LitigationKeyword> Keywords { get; set; } = [];
    public List<LitigationSnapshotSummary> Snapshots { get; set; } = [];
    public List<LitigationSnapshotSummary> CompletedSnapshotHistory { get => Snapshots; set => Snapshots = value; }

    public bool IsAuthoritativeCoverage { get; set; }
    public string CoverageSummaryText { get; set; } = string.Empty;

    public int UniqueCasesCount { get; set; }
    public int TotalObservationsCount { get; set; }
    public int TotalOrders { get; set; }
    public int DownloadedOrders { get; set; }
    public int PendingOrders { get; set; }
    public int FailedOrders { get; set; }
    public int ExpiredOrders { get; set; }
}

public sealed record LitigationSnapshotSummary(long SnapshotId, DateTime RetrievedUtc, string ReportHash, int CasesPersistedCount, BprReportFormat Format);

public sealed class LitigationCaseCardViewModel
{
    public long LitigationCaseId { get; set; }
    public string? CaseNumber { get; set; }
    public string? Cnr { get; set; }
    public string? CspId { get; set; }
    public string? ProviderCaseId { get; set; }

    public string? Court { get; set; }
    public string? Bench { get; set; }
    public string? CourtCategory { get; set; }
    public string? State { get; set; }
    public string? District { get; set; }

    public string? CaseType { get; set; }
    public string? CaseYear { get; set; }
    public string? CaseStage { get; set; }
    public string? CaseStatus { get; set; }
    public LitigationCaseStatusBucket StatusBucket { get; set; } = LitigationCaseStatusBucket.Unknown;

    public string? Act { get; set; }
    public string? ProceedingType { get; set; }
    public string? Direction { get; set; }

    public string? FilingDate { get; set; }
    public string? LastHearingDate { get; set; }
    public string? NextHearingDate { get; set; }
    public string? DecisionDate { get; set; }

    public List<string> Petitioners { get; set; } = [];
    public List<string> Respondents { get; set; } = [];
    public List<string> PetitionerAdvocates { get; set; } = [];
    public List<string> RespondentAdvocates { get; set; } = [];

    public List<LitigationOrderRowViewModel> Orders { get; set; } = [];
    public LitigationCaseAiAnalysisViewModel? Analysis { get; set; }
    public bool IsAnalysisStaleComparedToCase { get; set; }

    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
}

public sealed class LitigationOrderRowViewModel
{
    public long LitigationCaseOrderId { get; set; }
    public long? LitigationOrderDocumentId { get; set; }
    public string? OrderDate { get; set; }
    public string? OrderType { get; set; }
    public LitigationOrderDocumentStatus? DocumentStatus { get; set; }
    public bool IsExpired => DocumentStatus == LitigationOrderDocumentStatus.Expired;
    public string? FailureReason { get; set; }
    public int RefreshCount { get; set; }
}

public sealed class LitigationCaseAiAnalysisViewModel
{
    public long LitigationCaseAiAnalysisId { get; set; }
    public LitigationAiAnalysisItemStatus Status { get; set; }
    public string? RiskLevel { get; set; }
    public string? Summary { get; set; }
    public List<string> KeyIssues { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
    public List<string> Citations { get; set; } = [];
    public string? RecommendedAction { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string? FailureReason { get; set; }
    public int RunNumber { get; set; }
}

public sealed class LitigationPortfolioAiAnalysisViewModel
{
    public long LitigationPortfolioAiAnalysisId { get; set; }
    public LitigationAiAnalysisItemStatus Status { get; set; }
    public string? RiskLevel { get; set; }
    public string? Summary { get; set; }
    public List<string> KeyFindings { get; set; } = [];
    public List<string> Unknowns { get; set; } = [];
    public DateTime? CompletedUtc { get; set; }
    public string? FailureReason { get; set; }
    public int RunNumber { get; set; }
}
