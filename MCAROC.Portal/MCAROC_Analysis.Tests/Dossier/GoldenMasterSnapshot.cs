using MCAROC_Analysis.Models;
using MCAROC_Analysis.Models.Dossier;

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
    decimal? TotalOpenChargeAmount,
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
    string[] FindingCodesInDisplayOrder,
    string[] MetricGroupTitles,
    string[] DataSufficiencyNoteCodes)
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
        SecurityTypeLabelsByCharge: vm.Charges.OrderBy(c => c.RocChargeNumber).ToDictionary(
            c => c.RocChargeNumber,
            c => RequestDetailsViewModel.SecurityTypeLabels(c).ToArray()),
        LitigationFiledAgainstCount: vm.LitigationFiledAgainstCount,
        LitigationFiledByCount: vm.LitigationFiledByCount,
        LitigationRoleNotDeterminedCount: vm.LitigationRoleNotDeterminedCount,
        LitigationRoleByCaseNumber: vm.Litigations
            .Where(l => l.CaseNumber is not null).OrderBy(l => l.CaseNumber)
            .ToDictionary(l => l.CaseNumber!, l => vm.RoleFor(l).ToString()),
        StandaloneFyCount: vm.FinancialYears.Count,
        ConsolidatedFyCount: vm.ConsolidatedFinancialYears.Count,
        LatestFinancialYear: vm.LatestFinancialYear,
        LatestRevenue: vm.LatestRevenue,
        RevenueYoYPercent: vm.RevenueYoYPercent,
        ActiveDirectorCount: vm.ActiveDirectorCount,
        FindingCodesInDisplayOrder: vm.AnalysisFindings.Select(f => f.Code).ToArray(),
        MetricGroupTitles: vm.KeyMetrics.Select(g => g.Title).ToArray(),
        DataSufficiencyNoteCodes: vm.DataSufficiencyNotes.Select(n => n.Code).OrderBy(c => c).ToArray());

    public static GoldenMasterSnapshot From(DossierModel m) => new(
        OpenChargeCount: m.Charges.OpenCount,
        SatisfiedChargeCount: m.Charges.SatisfiedCount,
        TotalChargeCount: m.Charges.TotalCount,
        ChargeHolderCount: m.Charges.HolderCount,
        ModifiedChargeCount: m.Charges.ModifiedCount,
        MaterialEnhancementFindingCount: m.Charges.MaterialEnhancementFindingCount,
        TotalOpenChargeAmount: m.Charges.TotalOpenAmount,
        LargestChargeAmount: m.Charges.LargestAmount,
        LenderConcentrationHolders: m.Charges.LenderConcentration.Select(r => r.Holder).ToArray(),
        SecurityTypeLabelsByCharge: m.Charges.All.OrderBy(c => c.RocChargeNumber).ToDictionary(
            c => c.RocChargeNumber, c => m.Charges.SecurityTypeLabels(c).ToArray()),
        LitigationFiledAgainstCount: m.Litigation.FiledAgainstCount,
        LitigationFiledByCount: m.Litigation.FiledByCount,
        LitigationRoleNotDeterminedCount: m.Litigation.NotDeterminedCount,
        LitigationRoleByCaseNumber: m.Litigation.All
            .Where(l => l.CaseNumber is not null).OrderBy(l => l.CaseNumber)
            .ToDictionary(l => l.CaseNumber!, l => m.Litigation.RoleFor(l).ToString()),
        StandaloneFyCount: m.Financials.Standalone.Count,
        ConsolidatedFyCount: m.Financials.Consolidated.Count,
        LatestFinancialYear: m.Financials.LatestYear,
        LatestRevenue: m.Financials.LatestRevenue,
        RevenueYoYPercent: m.Financials.RevenueYoYPercent,
        ActiveDirectorCount: m.Corporate.ActiveDirectorCount,
        FindingCodesInDisplayOrder: m.ExecSummary.FindingsInDisplayOrder.Select(f => f.Code).ToArray(),
        MetricGroupTitles: m.Metrics.Select(g => g.Title).ToArray(),
        DataSufficiencyNoteCodes: m.ExecSummary.NotAssessed.Select(n => n.Code).OrderBy(c => c).ToArray());
}
