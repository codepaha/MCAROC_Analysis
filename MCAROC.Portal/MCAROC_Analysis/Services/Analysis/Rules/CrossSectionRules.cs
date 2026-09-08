using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Named, reproducible combination rules — not left to Gemini to invent. Each states its own
/// severity explicitly (never silently reuses a component finding's severity). Component lookups exclude
/// Historical findings — a resolved past event shouldn't drive a live cross-section flag.</summary>
public static class CrossSectionRules
{
    public const string FinancialStressCode = "CROSS_FINANCIAL_STRESS";
    public const string SupplierPaymentPressureCode = "CROSS_SUPPLIER_PAYMENT_PRESSURE";
    public const string GovernanceTransitionCode = "CROSS_GOVERNANCE_TRANSITION";
    public const string BorrowingSecurityReviewCode = "CROSS_BORROWING_SECURITY_REVIEW";
    public const string LiquidityPayrollStressCode = "CROSS_LIQUIDITY_PAYROLL_STRESS";

    public static List<FindingDraft> Evaluate(IReadOnlyList<FindingDraft> findings)
    {
        var results = new List<FindingDraft>();
        FindingDraft? Find(string code) => findings.FirstOrDefault(f => f.Code == code && f.TemporalStatus != TemporalStatus.Historical);
        FindingDraft? FindAtLeast(string code, FindingSeverity min) =>
            findings.FirstOrDefault(f => f.Code == code && f.TemporalStatus != TemporalStatus.Historical && f.Severity >= min);

        var revenueTrend = Find(FinancialRules.RevenueTrendGroup);
        var profitabilityTrend = Find(FinancialRules.ProfitabilityTrendGroup);
        var interestCoverage = Find(FinancialRules.InterestCoverageCriticalCode);
        if (revenueTrend is not null && profitabilityTrend is not null && interestCoverage is not null)
            results.Add(BuildCrossSection(FinancialStressCode, FindingSeverity.Critical, "Financial Stress",
                "Revenue contraction, weakening operating profitability, and inadequate interest coverage are present together, indicating broad-based financial stress.",
                [revenueTrend.Code, profitabilityTrend.Code, interestCoverage.Code]));

        var msmeMaterial = FindAtLeast(MsmeRules.MsmeDueMaterialCode, FindingSeverity.Review);
        var payableDaysHigh = FindAtLeast(FinancialRules.PayableDaysHighCode, FindingSeverity.Review);
        if (msmeMaterial is not null && payableDaysHigh is not null)
            results.Add(BuildCrossSection(SupplierPaymentPressureCode, FindingSeverity.Review, "Supplier Payment Pressure",
                "Material MSME dues outstanding alongside a material increase in payable days suggests pressure on supplier payments.",
                [msmeMaterial.Code, payableDaysHigh.Code]));

        var promoterExit = Find(DirectorRules.PromoterExitCode);
        var promoterDilution = Find(OwnershipRules.PromoterHoldingDeclineCode);
        if (promoterExit is not null && promoterDilution is not null)
            results.Add(BuildCrossSection(GovernanceTransitionCode, FindingSeverity.Review, "Governance Transition",
                "A promoter director exit combined with declining promoter shareholding suggests an ownership/governance transition underway.",
                [promoterExit.Code, promoterDilution.Code]));

        var enhancement = Find(ChargeRules.MaterialEnhancementCode);
        var registeredExposure = Find(ChargeRules.RegisteredExposureCode);
        if (enhancement is not null && registeredExposure is not null)
            results.Add(BuildCrossSection(BorrowingSecurityReviewCode, FindingSeverity.Review, "Borrowing & Security Review",
                "A material charge enhancement alongside high registered charge exposure to net worth warrants review of borrowing and security terms.",
                [enhancement.Code, registeredExposure.Code]));

        var epfoDelay = Find(EpfoRules.RepeatedPaymentDelayCode) ?? Find(EpfoRules.DelayWorseningCode);
        var epfoWorkforce = Find(EpfoRules.WorkforceDeclineCode) ?? Find(EpfoRules.PayrollContractionCode);
        var supplierSide = msmeMaterial ?? payableDaysHigh;
        var operatingWeakness = Find(FinancialRules.CfoNegativeCode) ?? profitabilityTrend ?? revenueTrend;
        var payrollStressGroups = new[] { epfoDelay, epfoWorkforce, supplierSide, operatingWeakness };
        var presentCount = payrollStressGroups.Count(x => x is not null);
        if (presentCount >= 3)
            results.Add(BuildCrossSection(LiquidityPayrollStressCode, presentCount == 4 ? FindingSeverity.Critical : FindingSeverity.Review,
                "Potential Liquidity and Payroll Stress",
                "EPFO contributions have become increasingly irregular while reported employee headcount has declined, alongside supplier-payment pressure and weakening operating "
                    + "performance. The combined pattern may indicate liquidity constraints or business contraction and warrants review of payroll regularity, statutory dues, and "
                    + "working-capital position.",
                payrollStressGroups.Where(x => x is not null).Select(x => x!.Code).ToList()));

        return results;
    }

    private static FindingDraft BuildCrossSection(string code, FindingSeverity severity, string title, string summary, IReadOnlyList<string> relatedCodes) =>
        new(FindingSection.CrossSection, severity, TemporalStatus.Current, code, title, summary, SupportingSignalCodes: relatedCodes);
}
