using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Dossier;

namespace MCAROC_Analysis.Models.Dossier;

/// <summary>The assembled, de-duplicated view of one analyzed request — the single source both the
/// client PDF (QuestPDF composer) and the restyled company page render from. Built by
/// <c>DossierAssembler</c>, cached by <c>DossierCache</c> keyed on the ingestion + analysis run ids.</summary>
public sealed record DossierModel(
    long RequestId,
    long IngestionRunId,
    long? AnalysisRunId,
    DossierCover Cover,
    DossierCorporate Corporate,
    DossierFinancials Financials,
    DossierCharges Charges,
    DossierCompliance Compliance,
    DossierLitigation Litigation,
    DossierExecSummary ExecSummary,
    IReadOnlyList<DossierSourceSheet> SourceSheets,
    SheetCoverage SourceCoverage,
    IReadOnlyList<MetricGroup> Metrics,
    // Added after every other field so the ~19 existing positional `new DossierModel(...)` call sites in
    // the test suite (metrics/calculation-assurance fixtures that don't care about the profile block)
    // keep compiling unchanged — only DossierAssembler passes this positionally; everyone else gets null.
    CompanyProfile? Profile = null,
    // The client's dossier-export preference (Client.IncludeLitigationInDossier), carried through unfiltered
    // — DossierModel itself is shared with the portal's on-screen company page (see DossierCache's own doc
    // comment), so no data here is ever redacted based on this flag. Only DossierPdfComposer reads it, to
    // decide what its own PDF output shows. Defaults true so every existing positional call site is unaffected.
    bool IncludeLitigation = true,
    // AI-synthesized read of the largest open charges (AiChargesNarrativeService), computed once at
    // analysis time — null when the AI call failed or hasn't run for this analysis run yet. Appended last,
    // same append-only precedent as Profile/IncludeLitigation above.
    Services.Analysis.ChargesNarrative? ChargesNarrative = null);

public sealed record DossierCover(
    string CompanyName, string? Cin, string? Pan, DateOnly? IncorporationDate, string? Status,
    string ClientName, DateTime ReportDate, DateTime? McaDataAsOf, DateTime? SourceSnapshotDate = null);

public sealed record DossierCorporate(
    IReadOnlyList<Director> Directors,
    IReadOnlyList<CompanyOfficer> Officers,
    IReadOnlyList<Shareholding> Shareholders,
    IReadOnlyList<RelatedCorporate> RelatedCorporates,
    IReadOnlyList<SecurityAllotment> SecurityAllotments,
    IReadOnlyList<DirectorAssignmentHistory> DesignationHistory,
    IReadOnlyList<DirectorAssociation> OtherDirectorships,
    CompanyStructure? Structure,
    decimal? PaidUpCapital,
    IReadOnlyList<ShareholdingPatternRow> ShareholdingPattern,
    IReadOnlyList<RelatedPartyTransaction> RelatedPartyTransactions,
    // Appended after every existing field, nullable with a normalizing list property, so the ~17
    // existing positional `new DossierCorporate(...)` test call sites keep compiling unchanged (#215).
    IReadOnlyList<ProprietorshipAssociation>? Proprietorship = null)
{
    public int ActiveDirectorCount => Directors.Count(d => d.CessationDate is null);
    public IReadOnlyList<ProprietorshipAssociation> ProprietorshipList => Proprietorship ?? [];
}

public sealed record DossierFinancials(
    IReadOnlyList<FinancialYearData> Standalone,
    IReadOnlyList<FinancialYearData> Consolidated,
    IReadOnlyList<FinancialFact> Facts,
    IReadOnlyList<FinancialParameter> Parameters,
    IReadOnlyList<AuditorObservation> AuditorObservations,
    IReadOnlyList<PeerComparisonMetric> PeerComparison,
    IReadOnlyList<PeerCompany>? ClosestPeers = null)
{
    public IReadOnlyList<PeerCompany> ClosestPeersList => ClosestPeers ?? [];
    public FinancialYearData? Latest => Standalone.OrderBy(f => f.FinancialYear).LastOrDefault();
    public int? LatestYear => Latest?.FinancialYear;
    public decimal? LatestRevenue => Latest?.Revenue;
    public decimal? RevenueYoYPercent => DossierComputations.RevenueYoYPercent(Standalone);
}

public sealed record DossierCharges(
    IReadOnlyList<RocCharge> All,
    IReadOnlyList<RocCharge> Open,
    IReadOnlyList<RocCharge> Satisfied,
    IReadOnlyList<LenderConcentrationRow> LenderConcentration,
    int MaterialEnhancementFindingCount)
{
    public int OpenCount => Open.Count;
    public int SatisfiedCount => Satisfied.Count;
    public int TotalCount => All.Count;
    public int HolderCount => DossierComputations.ChargeHolderCount(All);
    public int ModifiedCount => DossierComputations.ModifiedChargeCount(All);
    public decimal TotalOpenAmount => Open.Sum(c => c.CurrentAmount ?? 0m);
    public decimal? LargestAmount => All.Count == 0 ? null : All.Max(c => c.CurrentAmount);
    public IReadOnlyList<string> SecurityTypeLabels(RocCharge c) => DossierComputations.SecurityTypeLabels(c);
}

public sealed record DossierCompliance(
    IReadOnlyList<ComplianceRecord> Records,
    IReadOnlyList<MsmePayment> Msme,
    IReadOnlyList<GstRegistration> Gst,
    IReadOnlyList<EpfoContribution> Epfo,
    IReadOnlyList<EpfoEstablishment> EpfoEstablishments,
    IReadOnlyList<(string Bank, string? DefaulterType, decimal? Amount, int Quarters, DateOnly? Latest)> SuitFiledSummary,
    IReadOnlyList<CreditRating> CreditRatings);

public sealed record DossierLitigation(
    IReadOnlyList<Litigation> All,
    IReadOnlyList<LitigationThread> Threads,
    IReadOnlyDictionary<long, LitigationRole> RoleById,
    // Appended after every existing field, nullable with a normalizing list property, so the ~17
    // existing positional `new DossierLitigation(...)` test call sites keep compiling unchanged (#215).
    // A separate register from Litigation/All — see FinancialDisputeCase's own doc comment.
    IReadOnlyList<FinancialDisputeCase>? FinancialDisputeCases = null)
{
    public LitigationRole RoleFor(Litigation l) => RoleById.GetValueOrDefault(l.LitigationId, LitigationRole.NotDetermined);
    public int FiledAgainstCount => RoleById.Values.Count(v => v == LitigationRole.FiledAgainst);
    public int FiledByCount => RoleById.Values.Count(v => v == LitigationRole.FiledBy);
    public int NotDeterminedCount => RoleById.Values.Count(v => v == LitigationRole.NotDetermined);
    public int PendingCount => All.Count(DossierComputations.IsPendingLitigation);
    public int DisposedCount => All.Count - PendingCount;
    public IReadOnlyList<FinancialDisputeCase> FinancialDisputeCasesList => FinancialDisputeCases ?? [];
}

public sealed record DossierExecSummary(
    ReviewPriority? ReviewPriority,
    int CriticalCount, int ReviewCount, int WatchCount, int PositiveCount,
    IReadOnlyList<AnalysisFinding> FindingsInDisplayOrder,
    ExecutiveSummary? Structured,
    IReadOnlyList<DataSufficiencyNote> NotAssessed);

/// <summary>A deterministic rule the engine could not run, and why ("no charge has both a Creation and
/// a later Modification amount", "financials are FY2017 — distress rules gated"). The engine records
/// these as it evaluates; they render in <em>every</em> dossier variant so a "verified clean" result
/// is distinguishable from "not checked". AI-independent.</summary>
public sealed record DataSufficiencyNote(string Code, string Reason);

/// <summary>One worksheet's verbatim Layer-0 rows (<see cref="MCAROC_Analysis.Data.Entities.SourceRow"/>),
/// in workbook / sheet / row order — the "Full source" and "Source record" annexures render straight
/// from these with no typing, de-duplication or clipping.</summary>
public sealed record DossierSourceSheet(
    string WorkbookRole,
    string WorkbookLabel,
    int SheetIndex,
    string SheetName,
    IReadOnlyList<DossierSourceRow> Rows)
{
    /// <summary>The widest row's cell count — the raw table renders this many value columns.</summary>
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Cells.Count);
}

/// <summary>One raw row: the Excel row number the person would see, and the ordered cell values
/// exactly as extracted (a null/blank cell stays null).</summary>
public sealed record DossierSourceRow(int RowNumber, IReadOnlyList<string?> Cells);
