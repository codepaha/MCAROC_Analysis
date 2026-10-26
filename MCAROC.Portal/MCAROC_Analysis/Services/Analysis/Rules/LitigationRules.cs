using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Pending litigation against the company, over the litigation service's cases — the set the Litigation tab lists (see
/// <see cref="LitigationEvidence"/>). The company's side is taken as the case page shows it (its own name, current or former, among the
/// petitioners or the respondents, else the provider's direction); a pending case whose side is not stated, or that names the company on
/// both sides, caps at Watch, since Review implies a confirmed adverse signal the record does not give.</summary>
public static class LitigationRules
{
    public const string PendingAgainstCompanyCode = "LITIGATION_PENDING_AGAINST_COMPANY";
    public const string RoleUncertainCode = "LITIGATION_ROLE_UNCERTAIN";

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx)
    {
        var lit = ctx.Litigation;
        if (!lit.Searched)
            return [RuleEvaluationOutcome.NotEvaluated(PendingAgainstCompanyCode, "The litigation search has not completed yet.")];

        var againstCompany = new List<LitigationCase>();
        var roleUncertain = new List<LitigationCase>();
        foreach (var c in lit.Cases.Where(LitigationEvidence.IsPending))
        {
            switch (lit.SideOf(c))
            {
                case LitigationCompanySide.AgainstCompany: againstCompany.Add(c); break;
                case LitigationCompanySide.Unknown or LitigationCompanySide.Both: roleUncertain.Add(c); break;
                // By the company: suing someone else is not, on its own, an adverse signal.
            }
        }

        return
        [
            againstCompany.Count > 0
                ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Litigation, FindingSeverity.Review, TemporalStatus.Current,
                    PendingAgainstCompanyCode, "Pending Litigation Against Company",
                    $"{againstCompany.Count} pending case(s) name the company as the respondent.",
                    MetricsJson: JsonSerializer.Serialize(new { count = againstCompany.Count, caseNumbers = againstCompany.Select(LitigationEvidence.Cite) }),
                    SourceReferenceJson: LitigationRef(againstCompany)))
                : RuleEvaluationOutcome.NotTriggered(),

            roleUncertain.Count > 0
                ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Litigation, FindingSeverity.Watch, TemporalStatus.Current,
                    RoleUncertainCode, "Potential Litigation Requiring Role Verification",
                    $"{roleUncertain.Count} pending case(s) do not state whether the company is the petitioner or the respondent.",
                    MetricsJson: JsonSerializer.Serialize(new { count = roleUncertain.Count, caseNumbers = roleUncertain.Select(LitigationEvidence.Cite) }),
                    SourceReferenceJson: LitigationRef(roleUncertain)))
                : RuleEvaluationOutcome.NotTriggered()
        ];
    }

    /// <summary>Evidence link back to the exact cases a finding is about. Shape matches AnalysisFinding.SourceReferenceJson.</summary>
    internal static string LitigationRef(IEnumerable<LitigationCase> cases) => JsonSerializer.Serialize(new
    {
        entityType = nameof(LitigationCase),
        entityIds = cases.Select(c => c.LitigationCaseId).Distinct().OrderBy(id => id).ToArray()
    });
}
