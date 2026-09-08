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
    public List<FinancialYearData> FinancialYears { get; set; } = [];
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
}
