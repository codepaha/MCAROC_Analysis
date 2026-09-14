using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Data.Entities;

/// <summary>One immutable, internal-only record of a single audited calculation — a straight copy of a
/// <see cref="MetricResult"/> the dossier/portal already computed and shows, plus provenance and a
/// tamper-evidence hash. A correction always produces a new <see cref="CalculationAuditSnapshot"/> (a new
/// IngestionRun/AnalysisRun), never an in-place edit of an existing row — there are no update columns
/// here at all.</summary>
public class CalculationLedgerEntry
{
    public long CalculationLedgerEntryId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }
    public CalculationAuditSnapshot? Snapshot { get; set; }

    /// <summary>Stable machine key for this calculation, e.g. "FinancialTrend.RevenueYoY",
    /// "CapitalReconciliation.PaidUpVsShareCapital". Bumped only when a genuinely different formula
    /// replaces this key's meaning — ordinary formatting/display changes never touch it.</summary>
    public string CalculationKey { get; set; } = string.Empty;

    /// <summary>Bumped manually when this calculation's formula changes materially — mirrors
    /// AnalysisRun.RuleEngineVersion's convention.</summary>
    public string CalcVersion { get; set; } = "1.0";

    public string MetricLabel { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public MetricUnit Unit { get; set; }

    public decimal? ValueNumeric { get; set; }
    public string? ValueText { get; set; }

    /// <summary>Copied from MetricResult.InsufficiencyReason — a NotEvaluated-shaped input is recorded
    /// here, never silently dropped from the ledger.</summary>
    public string? InsufficiencyReason { get; set; }

    /// <summary>JSON array of the MetricResult's own "Entity.Field" input names.</summary>
    public string InputsJson { get; set; } = "[]";

    /// <summary>SHA-256 hex of the canonicalized resolved input values.</summary>
    public string InputHash { get; set; } = string.Empty;

    /// <summary>SHA-256 hex of (CalculationKey|Period|ValueNumeric|ValueText) — a drift fingerprint.</summary>
    public string OutputHash { get; set; } = string.Empty;

    public decimal? ToleranceAbsolute { get; set; }
    public decimal? TolerancePercent { get; set; }

    /// <summary>JSON array of CalculationSourceRowRef — the source rows the resolver could actually
    /// trace this value back to. Can be a real but partial subset (e.g. a 3-year CAGR citing only the
    /// latest two years) — partial-but-real is acceptable; a fabricated reference is not.</summary>
    public string SourceRowRefsJson { get; set; } = "[]";

    /// <summary>True when the resolver could not trace this entry's value back to any source row at
    /// all. A dedicated deterministic check (ProvenanceCompleteness, added in PR2) treats any such entry
    /// with a non-null ValueNumeric as an automatic NotEvaluated — an untraceable value never silently
    /// reads as "checked and fine."</summary>
    public bool HasUnresolvedProvenance { get; set; }

    public DateTime CreatedUtc { get; set; }
}
