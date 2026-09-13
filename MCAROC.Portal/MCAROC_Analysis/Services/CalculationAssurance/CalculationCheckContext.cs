using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Everything a deterministic check needs: the assembled DossierModel (for an independent
/// recompute over the same raw entities the ledger used) plus the ledger rows already persisted for this
/// snapshot (the audited subject each check cross-checks against).</summary>
public sealed record CalculationCheckContext(DossierModel Model, IReadOnlyList<CalculationLedgerEntry> LedgerEntries)
{
    public CalculationLedgerEntry? FindLedgerEntry(string calculationKey) =>
        LedgerEntries.FirstOrDefault(e => e.CalculationKey == calculationKey);

    public IReadOnlyList<CalculationLedgerEntry> FindLedgerEntriesByPrefix(string calculationKeyPrefix) =>
        LedgerEntries.Where(e => e.CalculationKey.StartsWith(calculationKeyPrefix, StringComparison.Ordinal)).ToList();
}
