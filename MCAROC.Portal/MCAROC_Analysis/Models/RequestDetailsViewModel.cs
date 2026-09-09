using MCAROC_Analysis.Data.Entities;

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
}

public class FilingSummaryViewModel
{
    public required McaFiling Filing { get; set; }
    public FilingCategory DominantCategory { get; set; }
    public McaFilingExtraction? Extraction { get; set; }
}
