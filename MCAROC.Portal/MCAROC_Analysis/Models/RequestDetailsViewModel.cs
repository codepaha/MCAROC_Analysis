using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

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

    // Phase 7.0 — completeness layer
    public List<CompanyOfficer> CompanyOfficers { get; set; } = [];
    public List<FinancialFact> FinancialFacts { get; set; } = [];

    // Phase 6 — the 12 additional workbook sheets.
    public CompanyStructure? Structure { get; set; }
    public List<RelatedCorporate> RelatedCorporates { get; set; } = [];
    public List<ComplianceRecord> ComplianceRecords { get; set; } = [];
    public List<FinancialParameter> FinancialParameters { get; set; } = [];
    public List<SecurityAllotment> SecurityAllotments { get; set; } = [];
    public List<ProprietorshipAssociation> ProprietorshipAssociations { get; set; } = [];
    public List<DirectorAssignmentHistory> DirectorAssignmentHistories { get; set; } = [];
    public List<PeerComparisonMetric> PeerComparisonMetrics { get; set; } = [];

    /// <summary>From <c>?charge=&lt;id&gt;</c> — the Charges &amp; Security tab expands this charge's drawer
    /// and scrolls it into view on load. Null when the query string carries no charge.</summary>
    public long? FocusChargeId { get; set; }

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

    // ---------------------------------------------------------------------
    // Phase 6 — Charges & Security workspace helpers (VM-computed, no extra DB work).
    // ---------------------------------------------------------------------

    /// <summary>Registered charge amount — never "outstanding loan". Label stays "Registered Charge Amount".</summary>
    public List<RocCharge> OpenChargesOrdered =>
        OpenCharges.OrderByDescending(c => c.CurrentAmount ?? 0m).ToList();

    private IEnumerable<RocCharge> SatisfiedCharges => Charges.Where(c =>
        c.SatisfactionDate is not null ||
        string.Equals(c.ChargeStatus, "Satisfied", StringComparison.OrdinalIgnoreCase));

    public List<RocCharge> SatisfiedChargesOrdered =>
        SatisfiedCharges.OrderByDescending(c => c.SatisfactionDate).ToList();

    public int SatisfiedChargeCount => Charges.Count - OpenChargeCount;
    public int ChargeHolderCount =>
        Charges.Select(c => c.LatestChargeHolderNormalized).Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

    /// <summary>Charges with more than one lifecycle event, or an explicit modification event.</summary>
    public int ModifiedChargeCount => Charges.Count(c =>
        c.Events.Count > 1 || c.Events.Any(e => e.EventType == ChargeEventType.Modification));

    public int MaterialEnhancementFindingCount =>
        AnalysisFindings.Count(f => f.Code == ChargeRules.MaterialEnhancementCode);

    /// <summary>Open charges grouped by normalized holder → (count, Σ registered amount, % of open registered
    /// amount). Sorted by amount desc. Drives the "lender concentration" table on the Summary section.</summary>
    public List<LenderConcentrationRow> LenderConcentration
    {
        get
        {
            var open = OpenChargesOrdered;
            var totalOpen = open.Sum(c => c.CurrentAmount ?? 0m);
            return open
                .GroupBy(c => string.IsNullOrWhiteSpace(c.LatestChargeHolderNormalized)
                    ? c.LatestChargeHolderRaw : c.LatestChargeHolderNormalized)
                .Select(g =>
                {
                    var amt = g.Sum(c => c.CurrentAmount ?? 0m);
                    return new LenderConcentrationRow(
                        g.Key, g.Count(), amt,
                        totalOpen == 0m ? null : Math.Round(amt / totalOpen * 100m, 1));
                })
                .OrderByDescending(r => r.RegisteredAmount)
                .ThenByDescending(r => r.OpenCount)
                .ToList();
        }
    }

    /// <summary>SecurityType values a charge's rollup names — parsed from LatestSecurityTypesJson, never re-derived.</summary>
    public static IReadOnlyList<string> SecurityTypeLabels(RocCharge c)
    {
        if (string.IsNullOrWhiteSpace(c.LatestSecurityTypesJson)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(c.LatestSecurityTypesJson) ?? []; }
        catch (JsonException) { return []; }
    }

    // ---------------------------------------------------------------------
    // Phase 6 — Litigation role attribution. Role is only ever taken from a Phase 3 finding's
    // SourceReferenceJson; "Filed By" is the residue of the rule's own deterministic partition
    // (pending + confirmed cases that the rule classified as neither "against" nor "role-uncertain").
    // A case named by no finding is "Role not determined" — never inferred at display time.
    // ---------------------------------------------------------------------

    private HashSet<long> LitigationIdsFromFinding(string code)
    {
        var f = AnalysisFindings.FirstOrDefault(x => x.Code == code);
        if (f?.SourceReferenceJson is not { } json) return [];
        try
        {
            var doc = JsonSerializer.Deserialize<JsonElement>(json);
            if (doc.ValueKind == JsonValueKind.Object && doc.TryGetProperty("entityIds", out var ids)
                && ids.ValueKind == JsonValueKind.Array)
                return ids.EnumerateArray().Where(e => e.TryGetInt64(out _)).Select(e => e.GetInt64()).ToHashSet();
        }
        catch (JsonException) { }
        return [];
    }

    private Dictionary<long, LitigationRole>? _litigationRoles;
    private Dictionary<long, LitigationRole> LitigationRoles
    {
        get
        {
            if (_litigationRoles is not null) return _litigationRoles;
            var against = LitigationIdsFromFinding(LitigationRules.PendingAgainstCompanyCode);
            var uncertain = LitigationIdsFromFinding(LitigationRules.RoleUncertainCode);
            var map = new Dictionary<long, LitigationRole>();
            foreach (var l in Litigations)
            {
                if (against.Contains(l.LitigationId)) map[l.LitigationId] = LitigationRole.FiledAgainst;
                else if (uncertain.Contains(l.LitigationId)) map[l.LitigationId] = LitigationRole.NotDetermined;
                else if (IsPendingLitigation(l) && l.MatchStatus == LitigationMatchStatus.Confirmed
                         && (against.Count > 0 || uncertain.Count > 0))
                    // The rule ran and partitioned pending confirmed cases; this one fell in neither
                    // adverse bucket ⇒ the rule classified it as filed BY the company.
                    map[l.LitigationId] = LitigationRole.FiledBy;
                else map[l.LitigationId] = LitigationRole.NotDetermined;
            }
            return _litigationRoles = map;
        }
    }

    private static bool IsPendingLitigation(Litigation l)
    {
        if (string.IsNullOrWhiteSpace(l.CaseStatus)) return false;
        var t = l.CaseStatus.ToLowerInvariant();
        return !new[] { "dispos", "clos", "resolv", "dismiss", "withdraw", "settl" }.Any(k => t.Contains(k));
    }

    public LitigationRole RoleFor(Litigation l) => LitigationRoles.GetValueOrDefault(l.LitigationId, LitigationRole.NotDetermined);

    public int LitigationFiledAgainstCount => LitigationRoles.Values.Count(v => v == LitigationRole.FiledAgainst);
    public int LitigationFiledByCount => LitigationRoles.Values.Count(v => v == LitigationRole.FiledBy);
    public int LitigationRoleNotDeterminedCount => LitigationRoles.Values.Count(v => v == LitigationRole.NotDetermined);

    public int LitigationPendingCount => Litigations.Count(IsPendingLitigation);
    public int LitigationDisposedCount => Litigations.Count - LitigationPendingCount;
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
