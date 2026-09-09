using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public static class CompanyProfileRules
{
    public const string StatusAdverseCode = "CORP_COMPANY_STATUS_ADVERSE";
    public const string ComplianceNonCompliantCode = "CORP_COMPLIANCE_NON_COMPLIANT";
    public const string StaleFinancialDataCode = "CORP_FINANCIAL_DATA_STALE";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx, RuleThresholds thresholds)
    {
        var outcomes = new List<RuleEvaluationOutcome>();
        var profile = ctx.CompanyProfile;

        if (profile is null)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(StatusAdverseCode, "No CompanyProfile record available."));
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(ComplianceNonCompliantCode, "No CompanyProfile record available."));
        }
        else
        {
            var status = CompanyStatusNormalizer.Normalize(profile.CompanyStatus);
            outcomes.Add(status switch
            {
                NormalizedCompanyStatus.Unknown => RuleEvaluationOutcome.NotEvaluated(
                    StatusAdverseCode, $"CompanyStatus '{profile.CompanyStatus}' did not match a known status category."),
                NormalizedCompanyStatus.StruckOff or NormalizedCompanyStatus.UnderLiquidation
                    or NormalizedCompanyStatus.Liquidated or NormalizedCompanyStatus.UnderCirp =>
                    RuleEvaluationOutcome.Triggered(new FindingDraft(
                        FindingSection.CompanyProfile, FindingSeverity.Critical, TemporalStatus.Current,
                        StatusAdverseCode, "Adverse Company Status",
                        $"Company status is recorded as '{profile.CompanyStatus}'.",
                        MetricsJson: JsonSerializer.Serialize(new { companyStatus = profile.CompanyStatus, normalized = status.ToString() }))),
                _ => RuleEvaluationOutcome.NotTriggered()
            });

            var compliance = ComplianceStatusNormalizer.Normalize(profile.ComplianceStatus);
            outcomes.Add(compliance switch
            {
                NormalizedComplianceStatus.Unknown => RuleEvaluationOutcome.NotEvaluated(
                    ComplianceNonCompliantCode, $"ComplianceStatus '{profile.ComplianceStatus}' did not match a known state."),
                NormalizedComplianceStatus.NonCompliant => RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.CompanyProfile, FindingSeverity.Review, TemporalStatus.Current,
                    ComplianceNonCompliantCode, "Non-Compliant Status",
                    $"Compliance status is recorded as '{profile.ComplianceStatus}'.",
                    MetricsJson: JsonSerializer.Serialize(new { complianceStatus = profile.ComplianceStatus }))),
                _ => RuleEvaluationOutcome.NotTriggered()
            });
        }

        if (ctx.LatestFinancialYear is null)
        {
            outcomes.Add(RuleEvaluationOutcome.NotEvaluated(StaleFinancialDataCode, "No FinancialYearData available."));
        }
        else if (ctx.FinancialDataAgeMonths is { } age && age > thresholds.StaleFinancialDataMonths)
        {
            outcomes.Add(RuleEvaluationOutcome.Triggered(new FindingDraft(
                FindingSection.CompanyProfile, FindingSeverity.Watch, TemporalStatus.Current,
                StaleFinancialDataCode, "Financial Data May Be Dated",
                $"The latest available financial year is FY{ctx.LatestFinancialYear.FinancialYear}, approximately {age} months old.",
                MetricsJson: JsonSerializer.Serialize(new { latestFinancialYear = ctx.LatestFinancialYear.FinancialYear, ageMonths = age }),
                PeriodLabel: $"FY{ctx.LatestFinancialYear.FinancialYear}")));
        }
        else
        {
            outcomes.Add(RuleEvaluationOutcome.NotTriggered());
        }

        return outcomes;
    }
}
