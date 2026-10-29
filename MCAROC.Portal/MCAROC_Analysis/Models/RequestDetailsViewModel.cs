using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Models;

/// <summary>Per-case litigation role for the Litigation tab. Only ever set from a Phase 3 rule finding's
/// SourceReferenceJson (or, for <see cref="FiledBy"/>, reconstructed from the rule's own deterministic
/// partition of pending confirmed cases) — never guessed at display time.</summary>
public enum LitigationRole { NotDetermined, FiledAgainst, FiledBy }

public class RequestDetailsViewModel
{
    public required McaRequest Request { get; set; }
    public required List<RequestDocument> Documents { get; set; }
    public IngestionRun? LatestRun { get; set; }
    public List<IngestionIssue> Issues { get; set; } = [];

    /// <summary>Which optional workbook sheets this run's upload contained — drives the conditional
    /// empty-state text ("not in this upload" vs "present, reported no records") and the "N of M
    /// optional sheets present" coverage line.</summary>
    public SheetCoverage SheetCoverage { get; set; } = SheetCoverage.Empty;

    public CompanyProfile? CompanyProfile { get; set; }
    public List<Director> Directors { get; set; } = [];
    public List<DirectorAssociation> DirectorAssociations { get; set; } = [];
    public List<Shareholding> Shareholdings { get; set; } = [];
    /// <summary>Standalone basis only — the Highlights props and Phase 3 both treat this as the primary set.</summary>
    public List<FinancialYearData> FinancialYears { get; set; } = [];
    public List<FinancialYearData> ConsolidatedFinancialYears { get; set; } = [];
    public List<RocCharge> Charges { get; set; } = [];
    /// <summary>Completed Gemini property-particulars extractions for these charges' texts, keyed by text hash.</summary>
    public IReadOnlyDictionary<string, PropertyParticularsExtraction> PropertyExtractions { get; set; } = new Dictionary<string, PropertyParticularsExtraction>();
    /// <summary>#364: the filed charge e-forms (XFA) linked to each charge, keyed by RocCharge.ChargeId.</summary>
    public IReadOnlyDictionary<long, IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.LinkedChargeForm>> ChargeForms { get; set; }
        = new Dictionary<long, IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.LinkedChargeForm>>();
    /// <summary>#377: the filing documents linked to each charge (open or satisfied), keyed by the register's charge number
    /// without leading zeros.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.ChargeDocumentRow>> ChargeDocuments { get; set; }
        = new Dictionary<string, IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.ChargeDocumentRow>>();
    /// <summary>#364 part 2: the property passages quoted from each linked document, keyed by FilingDocumentId.</summary>
    public IReadOnlyDictionary<long, MCAROC_Analysis.Services.PropertyParticulars.ChargeInstrumentAiResult> InstrumentPassages { get; set; }
        = new Dictionary<long, MCAROC_Analysis.Services.PropertyParticulars.ChargeInstrumentAiResult>();
    public List<MsmePayment> MsmePayments { get; set; } = [];
    public List<GstRegistration> GstRegistrations { get; set; } = [];
    public List<EpfoContribution> EpfoContributions { get; set; } = [];
    public List<EpfoEstablishment> EpfoEstablishments { get; set; } = [];
    public List<AuditorObservation> AuditorObservations { get; set; } = [];
    public LitigationTabViewModel? LitigationDataLake { get; set; }

    /// <summary>Litigation that touches what the open charges secure (see <c>ChargeLitigationLinker</c>); null
    /// when it could not be computed. Drives the Charges-tab badges/drawer and the Litigation-tab banner.</summary>
    public ChargeLitigationSummary? ChargeLitigation { get; set; }

    // Phase 7.0 — completeness layer
    public List<CompanyOfficer> CompanyOfficers { get; set; } = [];
    public List<FinancialFact> FinancialFacts { get; set; } = [];

    // Phase 8 — data-parity layer
    public List<CompanyEmail> CompanyEmails { get; set; } = [];
    public List<ShareholdingPatternRow> ShareholdingPattern { get; set; } = [];
    /// <summary>An LLP's partners' and body corporates' contributions (#539); empty for a company.</summary>
    public List<LlpContribution> LlpContributions { get; set; } = [];
    /// <summary>The words the page uses for the entity: LLPIN, partners, contribution for an LLP (#539).</summary>
    public EntityWording Wording => EntityWording.For(Request.EntityType);
    public List<CompanyNameHistory> NameHistory { get; set; } = [];
    /// <summary>#497: the changes of name the Registrar's certificates in the filings state (empty when none was read).</summary>
    public IReadOnlyList<MCAROC_Analysis.Services.McaFilings.CertificateNameChange> NameChangeCertificates { get; set; } = [];
    /// <summary>The name history checked against <see cref="NameChangeCertificates"/>: what the Name History table and the timeline show.</summary>
    public IReadOnlyList<MCAROC_Analysis.Services.McaFilings.NameChange> NameChanges =>
        MCAROC_Analysis.Services.McaFilings.NameChanges.Merge(NameHistory, CompanyProfile?.CompanyName ?? Request.CompanyName, NameChangeCertificates);
    public List<PrincipalBusinessActivity> PrincipalBusinessActivities { get; set; } = [];

    // Phase 8 Wave 4 — computed metrics (docs/analytics-catalogue.json). Populated per-tab by the
    // D-wave issues; the _KeyIndicators partial renders whichever groups a tab passes it.
    public List<MCAROC_Analysis.Models.Dossier.MetricGroup> KeyMetrics { get; set; } = [];

    /// <summary>Issue #97 — the unified corporate event timeline. Built by
    /// <see cref="MCAROC_Analysis.Services.Dossier.CorporateTimelineBuilder"/>, independent of
    /// <see cref="MCAROC_Analysis.Services.Dossier.DossierAssembler"/>/<c>DossierCache</c> so it stays
    /// populated even while a fresh re-ingest's analysis is still queued or running.</summary>
    public List<MCAROC_Analysis.Models.Dossier.CorporateTimelineEvent> Timeline { get; set; } = [];

    /// <summary>Deterministic checks the rule engine could not run, and why — same source
    /// (<c>DossierModel.ExecSummary.NotAssessed</c>) the dossier renders, so a "verified clean" result
    /// is never mistaken for "not checked". AI-independent.</summary>
    public List<MCAROC_Analysis.Models.Dossier.DataSufficiencyNote> DataSufficiencyNotes { get; set; } = [];

    // Phase 6 — the 12 additional workbook sheets.
    public CompanyStructure? Structure { get; set; }
    public List<RelatedCorporate> RelatedCorporates { get; set; } = [];
    public List<RelatedPartyTransaction> RelatedPartyTransactions { get; set; } = [];
    public List<CreditRating> CreditRatings { get; set; } = [];
    public List<ComplianceRecord> ComplianceRecords { get; set; } = [];
    public List<FinancialParameter> FinancialParameters { get; set; } = [];
    public List<SecurityAllotment> SecurityAllotments { get; set; } = [];
    public List<ProprietorshipAssociation> ProprietorshipAssociations { get; set; } = [];
    public List<DirectorAssignmentHistory> DirectorAssignmentHistories { get; set; } = [];
    public List<PeerComparisonMetric> PeerComparisonMetrics { get; set; } = [];
    public List<PeerCompany> PeerCompanies { get; set; } = [];

    /// <summary>From <c>?charge=&lt;id&gt;</c> — the Charges &amp; Security tab expands this charge's drawer
    /// and scrolls it into view on load. Null when the query string carries no charge.</summary>
    public long? FocusChargeId { get; set; }

    /// <summary>From <c>?focusCase=&lt;id&gt;</c> — the Litigation tab sets its page to include this case card
    /// and scrolls it into view on load. Null when the query string carries no case focus.</summary>
    public long? FocusCaseId { get; set; }

    /// <summary>The latest AnalysisRun (any Status) for this request, if one has ever started — the AI
    /// Analysis tab renders an in-progress/failed state until this reaches a terminal Completed/
    /// CompletedWithErrors status.</summary>
    public AnalysisRun? LatestAnalysisRun { get; set; }
    public List<AnalysisFinding> AnalysisFindings { get; set; } = [];
    public ExecutiveSummary? ExecutiveSummary { get; set; }

    /// <summary>The auto-fetch job that created this request from a bare CIN, if any — the Details page
    /// shows its progress panel (polling) until it reaches a terminal state.</summary>
    public AutoFetchJob? AutoFetchJob { get; set; }
    /// <summary>The name the request asked for when it differs from the company's registered name (#497): a former name, or a name a person
    /// resolved to a different-looking company. Null when they agree, or the request was identified by CIN/LLPIN.</summary>
    public string? RequestedAsName { get; set; }
    /// <summary>Whether <see cref="RequestedAsName"/> is a former name of the company (the resolution found it that way).</summary>
    public bool RequestedAsFormerName { get; set; }
    public bool HasFullAccess { get; set; }

    // MCA Filings (PDF) pipeline
    public McaFilingBatch? FilingBatch { get; set; }
    public List<FilingSummaryViewModel> FilingSummaries { get; set; } = [];
    /// <summary>Projected IDs for validating chat citations without materializing filing bodies.</summary>
    public HashSet<long>? AuthoritativeDocumentIds { get; set; }
    public Dictionary<FilingCategory, int> FilingCategoryCounts { get; set; } = [];
    /// <summary>#499: filed documents whose specific type is known (a chat request for a document type finds only these).</summary>
    public int FilingTypedCount { get; set; }
    /// <summary>#499: the chat panel's suggested questions, each only when this request's data answers it.</summary>
    public IReadOnlyList<string> ChatSuggestions { get; set; } = [];
    public Dictionary<TextExtractionMethod, int> TextExtractionMethodCounts { get; set; } = [];
    public int AiSuccessCount { get; set; }
    public int AiFailedCount { get; set; }
    public int ManualReviewFilingCount { get; set; }

    // Phase 4: Ask Documents
    public List<ChatMessage> ChatMessages { get; set; } = [];
    public int ChunkableDocumentCount { get; set; }
    /// <summary>#349: set when chat retrieval runs without its keyword half for some or all documents
    /// (<see cref="Services.Chat.FullTextSearchState.ChatNote"/>); null when keyword search covers everything.</summary>
    public string? KeywordSearchNote { get; set; }

    /// <summary>True when the page was read while a litigation import was still moving the live-data counter and was read again ignoring it: the
    /// litigation figures may then come from a slightly different moment than the rest of the page. The page says so.</summary>
    public bool LiveImportInProgress { get; set; }
    public int ChunkedDocumentCount { get; set; }
    public int AuthoritativeBatchChunkableCount { get; set; }
    public int AuthoritativeBatchChunkedCount { get; set; }
    public int AuthoritativeBatchChunkingFailedCount { get; set; }
    public int AuthoritativeBatchTotalChunks { get; set; }

    // ---------------------------------------------------------------------
    // Highlights — computed from the lists already loaded above (no extra DB work).
    // Used by the "Highlights" tab on Details.cshtml.
    // ---------------------------------------------------------------------

    /// <summary>Charges not yet satisfied (no satisfaction date and status isn't "Satisfied").</summary>
    private IEnumerable<RocCharge> OpenCharges => Charges.Where(DossierComputations.IsOpenCharge);

    public int OpenChargeCount => OpenCharges.Count();
    public int TotalChargeCount => Charges.Count;
    public decimal? LargestChargeAmount => Charges.Max(c => c.CurrentAmount);
    public bool AnyOpenChargeMissingAmount => OpenCharges.Any(c => c.CurrentAmount is null);
    public decimal? TotalOpenChargeAmount => AnyOpenChargeMissingAmount ? null : OpenCharges.Sum(c => c.CurrentAmount);

    public FinancialYearData? LatestFinancials => FinancialYears.OrderBy(f => f.FinancialYear).LastOrDefault();

    public int? LatestFinancialYear => LatestFinancials?.FinancialYear;
    public decimal? LatestRevenue => LatestFinancials?.Revenue;
    public decimal? LatestNetWorth => LatestFinancials?.NetWorth;
    public decimal? LatestPat => LatestFinancials?.Pat;
    public decimal? LatestTotalDebt => LatestFinancials?.TotalDebt;

    /// <summary>Year-on-year revenue change, percent — null unless both years have a non-zero revenue.</summary>
    public decimal? RevenueYoYPercent => DossierComputations.RevenueYoYPercent(FinancialYears);

    public int ActiveDirectorCount => Directors.Count(d => d.CessationDate is null);
    /// <summary>The company's cases from the litigation service: the case list's own set, before any filter. Null until a search has
    /// completed. The workbook's own legal-history sheets are not read (owner, 2026-10-26), so this is the only litigation count.</summary>
    public int? LitigationCaseCount => LitigationDataLake is { AuthoritativeSnapshot: not null } lake ? lake.SourceCoverage.UniqueCasesCount : null;

    // ---------------------------------------------------------------------
    // Phase 6 — Charges & Security workspace helpers (VM-computed, no extra DB work).
    // ---------------------------------------------------------------------

    /// <summary>Registered charge amount — never "outstanding loan". Label stays "Registered Charge Amount".</summary>
    public List<RocCharge> OpenChargesOrdered => DossierComputations.OpenChargesByAmount(Charges);
    public List<RocCharge> SatisfiedChargesOrdered => DossierComputations.SatisfiedChargesBySatisfaction(Charges);

    public int SatisfiedChargeCount => Charges.Count - OpenChargeCount;
    public int ChargeHolderCount => DossierComputations.ChargeHolderCount(Charges);
    public int ModifiedChargeCount => DossierComputations.ModifiedChargeCount(Charges);

    public int MaterialEnhancementFindingCount =>
        AnalysisFindings.Count(f => f.Code == ChargeRules.MaterialEnhancementCode);

    /// <summary>Open charges grouped by normalized holder → (count, Σ registered amount, % of open registered
    /// amount). Sorted by amount desc. Drives the "lender concentration" table on the Summary section.</summary>
    public List<LenderConcentrationRow> LenderConcentration => DossierComputations.LenderConcentration(Charges);

    /// <summary>SecurityType values a charge's rollup names — parsed from LatestSecurityTypesJson, never re-derived.</summary>
    public static IReadOnlyList<string> SecurityTypeLabels(RocCharge c) => DossierComputations.SecurityTypeLabels(c);

}

/// <summary>One lender's share of the open registered-charge portfolio. PctOfOpenAmount is null only when
/// the total open registered amount is zero (every open charge has a null amount).</summary>
public record LenderConcentrationRow(string Holder, int OpenCount, decimal RegisteredAmount, decimal? PctOfOpenAmount);

public class FilingSummaryViewModel
{
    public required McaFiling Filing { get; set; }
    public FilingCategory DominantCategory { get; set; }
    public McaFilingExtraction? Extraction { get; set; }
}
