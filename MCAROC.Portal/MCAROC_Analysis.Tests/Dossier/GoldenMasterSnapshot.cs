using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Tests.Dossier;

/// <summary>A stable projection of the values the current <see cref="RequestDetailsViewModel"/> computes
/// for the <see cref="DossierTestSeed"/> graph. Captured once to a checked-in fixture; the
/// <c>DossierAssembler</c> refactor must reproduce it exactly (that's the whole point — a move-and-
/// refactor of the Phase 6 load queries is the highest regression risk in Phase 7).</summary>
public sealed record GoldenMasterSnapshot(
    int OpenChargeCount,
    int SatisfiedChargeCount,
    int TotalChargeCount,
    int ChargeHolderCount,
    int ModifiedChargeCount,
    int MaterialEnhancementFindingCount,
    decimal TotalOpenChargeAmount,
    decimal? LargestChargeAmount,
    string[] LenderConcentrationHolders,
    Dictionary<string, string[]> SecurityTypeLabelsByCharge,
    int LitigationFiledAgainstCount,
    int LitigationFiledByCount,
    int LitigationRoleNotDeterminedCount,
    Dictionary<string, string> LitigationRoleByCaseNumber,
    int StandaloneFyCount,
    int ConsolidatedFyCount,
    int? LatestFinancialYear,
    decimal? LatestRevenue,
    decimal? RevenueYoYPercent,
    int ActiveDirectorCount,
    string[] FindingCodesInDisplayOrder)
{
    public static GoldenMasterSnapshot From(RequestDetailsViewModel vm) => new(
        OpenChargeCount: vm.OpenChargeCount,
        SatisfiedChargeCount: vm.SatisfiedChargeCount,
        TotalChargeCount: vm.TotalChargeCount,
        ChargeHolderCount: vm.ChargeHolderCount,
        ModifiedChargeCount: vm.ModifiedChargeCount,
        MaterialEnhancementFindingCount: vm.MaterialEnhancementFindingCount,
        TotalOpenChargeAmount: vm.TotalOpenChargeAmount,
        LargestChargeAmount: vm.LargestChargeAmount,
        LenderConcentrationHolders: vm.LenderConcentration.Select(r => r.Holder).ToArray(),
        SecurityTypeLabelsByCharge: vm.Charges.ToDictionary(
            c => c.RocChargeNumber,
            c => RequestDetailsViewModel.SecurityTypeLabels(c).ToArray()),
        LitigationFiledAgainstCount: vm.LitigationFiledAgainstCount,
        LitigationFiledByCount: vm.LitigationFiledByCount,
        LitigationRoleNotDeterminedCount: vm.LitigationRoleNotDeterminedCount,
        LitigationRoleByCaseNumber: vm.Litigations
            .Where(l => l.CaseNumber is not null)
            .ToDictionary(l => l.CaseNumber!, l => vm.RoleFor(l).ToString()),
        StandaloneFyCount: vm.FinancialYears.Count,
        ConsolidatedFyCount: vm.ConsolidatedFinancialYears.Count,
        LatestFinancialYear: vm.LatestFinancialYear,
        LatestRevenue: vm.LatestRevenue,
        RevenueYoYPercent: vm.RevenueYoYPercent,
        ActiveDirectorCount: vm.ActiveDirectorCount,
        FindingCodesInDisplayOrder: vm.AnalysisFindings.Select(f => f.Code).ToArray());
}
