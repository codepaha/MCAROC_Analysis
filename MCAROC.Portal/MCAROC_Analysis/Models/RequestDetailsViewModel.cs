using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;

namespace MCAROC_Analysis.Models;

public class RequestDetailsViewModel
{
    public required McaRequest Request { get; set; }
    public required List<RequestDocument> Documents { get; set; }
    public IngestionRun? LatestRun { get; set; }
    public List<IngestionIssue> Issues { get; set; } = [];

    public CompanyProfile? CompanyProfile { get; set; }
    public List<Director> Directors { get; set; } = [];
    public List<DirectorAssociation> DirectorAssociations { get; set; } = [];
    public List<Shareholding> Shareholdings { get; set; } = [];
    /// <summary>Standalone basis only — the Highlights props and Phase 3 both treat this as the primary set.</summary>
    public List<FinancialYearData> FinancialYears { get; set; } = [];
    public List<FinancialYearData> ConsolidatedFinancialYears { get; set; } = [];
    public List<RocCharge> Charges { get; set; } = [];
    public List<MsmePayment> MsmePayments { get; set; } = [];
    public List<GstRegistration> GstRegistrations { get; set; } = [];
    public List<EpfoContribution> EpfoContributions { get; set; } = [];
    public List<AuditorObservation> AuditorObservations { get; set; } = [];
    public List<Litigation> Litigations { get; set; } = [];

    /// <summary>The latest AnalysisRun (any Status) for this request, if one has ever started — the AI
    /// Analysis tab renders an in-progress/failed state until this reaches a terminal Completed/
    /// CompletedWithErrors status.</summary>
    public AnalysisRun? LatestAnalysisRun { get; set; }
    public List<AnalysisFinding> AnalysisFindings { get; set; } = [];
    public ExecutiveSummary? ExecutiveSummary { get; set; }

    // MCA Filings (PDF) pipeline
    public McaFilingBatch? FilingBatch { get; set; }
    public List<FilingSummaryViewModel> FilingSummaries { get; set; } = [];
    public Dictionary<FilingCategory, int> FilingCategoryCounts { get; set; } = [];
    public Dictionary<TextExtractionMethod, int> TextExtractionMethodCounts { get; set; } = [];
    public int AiSuccessCount { get; set; }
    public int AiFailedCount { get; set; }
    public int ManualReviewFilingCount { get; set; }

    // Phase 4: Ask Documents
    public List<ChatMessage> ChatMessages { get; set; } = [];
    public int ChunkableDocumentCount { get; set; }
    public int ChunkedDocumentCount { get; set; }

    // ---------------------------------------------------------------------
    // Highlights — computed from the lists already loaded above (no extra DB work).
    // Used by the "Highlights" tab on Details.cshtml.
    // ---------------------------------------------------------------------

    /// <summary>Charges not yet satisfied (no satisfaction date and status isn't "Satisfied").</summary>
    private IEnumerable<RocCharge> OpenCharges => Charges.Where(c =>
        c.SatisfactionDate is null &&
        !string.Equals(c.ChargeStatus, "Satisfied", StringComparison.OrdinalIgnoreCase));

    public int OpenChargeCount => OpenCharges.Count();
    public int TotalChargeCount => Charges.Count;
    public decimal? LargestChargeAmount => Charges.Max(c => c.CurrentAmount);
    public decimal TotalOpenChargeAmount => OpenCharges.Sum(c => c.CurrentAmount ?? 0m);

    private List<FinancialYearData> FinancialYearsAsc => FinancialYears.OrderBy(f => f.FinancialYear).ToList();
    public FinancialYearData? LatestFinancials => FinancialYearsAsc.LastOrDefault();
    private FinancialYearData? PriorFinancials =>
        FinancialYearsAsc.Count >= 2 ? FinancialYearsAsc[^2] : null;

    public int? LatestFinancialYear => LatestFinancials?.FinancialYear;
    public decimal? LatestRevenue => LatestFinancials?.Revenue;
    public decimal? LatestNetWorth => LatestFinancials?.NetWorth;
    public decimal? LatestPat => LatestFinancials?.Pat;
    public decimal? LatestTotalDebt => LatestFinancials?.TotalDebt;

    /// <summary>Year-on-year revenue change, percent — null unless both years have a non-zero revenue.</summary>
    public decimal? RevenueYoYPercent
    {
        get
        {
            var current = LatestFinancials?.Revenue;
            var prior = PriorFinancials?.Revenue;
            if (current is null || prior is null || prior.Value == 0m) return null;
            return Math.Round((current.Value - prior.Value) / Math.Abs(prior.Value) * 100m, 1);
        }
    }

    public int ActiveDirectorCount => Directors.Count(d => d.CessationDate is null);
    public int LitigationCount => Litigations.Count;
}

public class FilingSummaryViewModel
{
    public required McaFiling Filing { get; set; }
    public FilingCategory DominantCategory { get; set; }
    public McaFilingExtraction? Extraction { get; set; }
}
