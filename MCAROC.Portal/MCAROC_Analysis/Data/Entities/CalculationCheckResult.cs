namespace MCAROC_Analysis.Data.Entities;

/// <summary>One append-only deterministic check outcome (built in PR2 — the entity ships in PR1's
/// migration so the whole calculation-assurance schema lands in a single migration). Cited ledger
/// evidence goes through <see cref="CalculationCheckResultLedgerLink"/>, never an unconstrained JSON id
/// list, so the database itself enforces that a check's evidence belongs to the same snapshot.</summary>
public class CalculationCheckResult
{
    public long CalculationCheckResultId { get; set; }

    public long CalculationAuditSnapshotId { get; set; }
    public CalculationAuditSnapshot? Snapshot { get; set; }

    public string CheckKey { get; set; } = string.Empty;
    public string CheckVersion { get; set; } = "1.0";

    public CalculationCheckStatus Status { get; set; }

    /// <summary>Set only when Status == Triggered.</summary>
    public CalculationDiscrepancySeverity? Severity { get; set; }

    /// <summary>Structured expected/actual/delta/tolerance detail — internal-only.</summary>
    public string? DetailJson { get; set; }

    public string? NotEvaluatedReason { get; set; }

    public DateTime RanUtc { get; set; }
}
