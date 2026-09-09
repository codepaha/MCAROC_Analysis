using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Litigation has no structured ClaimAmount or CompanyRole field (Phase 3.1 candidate) — role is
/// inferred from free-text Litigants via a best-effort heuristic. MatchStatus=Probable/Uncertain can never
/// yield Critical; role-ambiguous cases cap at Watch, not Review, since Review implies a confirmed adverse
/// signal the data doesn't actually support yet.</summary>
public static partial class LitigationRules
{
    public const string PendingAgainstCompanyCode = "LITIGATION_PENDING_AGAINST_COMPANY";
    public const string RoleUncertainCode = "LITIGATION_ROLE_UNCERTAIN";

    private static readonly string[] ClosedKeywords = ["dispos", "clos", "resolv", "dismiss", "withdraw", "settl"];

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx)
    {
        if (ctx.Litigations.Count == 0)
            return [RuleEvaluationOutcome.NotEvaluated(PendingAgainstCompanyCode, "No Litigation records available.")];

        var companyName = ctx.CompanyProfile?.CompanyName;
        var pending = ctx.Litigations.Where(l => IsPending(l.CaseStatus)).ToList();
        if (pending.Count == 0)
            return [RuleEvaluationOutcome.NotTriggered(), RuleEvaluationOutcome.NotTriggered()];

        var againstCompany = new List<Litigation>();
        var roleUncertain = new List<Litigation>();

        foreach (var lit in pending)
        {
            // Probable/Uncertain matches never count as a confirmed adverse signal, regardless of role.
            if (lit.MatchStatus != LitigationMatchStatus.Confirmed)
            {
                roleUncertain.Add(lit);
                continue;
            }

            var role = DetermineRole(lit.Litigants, companyName);
            if (role == LitigantRole.AgainstCompany)
                againstCompany.Add(lit);
            else if (role == LitigantRole.Unknown)
                roleUncertain.Add(lit);
            // role == ByCompany: the company suing someone else is not, on its own, an adverse signal — not added to either bucket.
        }

        var outcomes = new List<RuleEvaluationOutcome>
        {
            againstCompany.Count > 0
                ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Litigation, FindingSeverity.Review, TemporalStatus.Current,
                    PendingAgainstCompanyCode, "Pending Litigation Against Company",
                    $"{againstCompany.Count} pending confirmed case(s) appear to be filed against the company.",
                    MetricsJson: JsonSerializer.Serialize(new { count = againstCompany.Count, caseNumbers = againstCompany.Select(l => l.CaseNumber) }),
                    SourceReferenceJson: LitigationRef(againstCompany)))
                : RuleEvaluationOutcome.NotTriggered(),

            roleUncertain.Count > 0
                ? RuleEvaluationOutcome.Triggered(new FindingDraft(
                    FindingSection.Litigation, FindingSeverity.Watch, TemporalStatus.Current,
                    RoleUncertainCode, "Potential Litigation Requiring Role Verification",
                    $"{roleUncertain.Count} pending case(s) could not be confidently classified as filed by or against the company from available text, or are probable/uncertain matches.",
                    MetricsJson: JsonSerializer.Serialize(new { count = roleUncertain.Count, caseNumbers = roleUncertain.Select(l => l.CaseNumber) }),
                    SourceReferenceJson: LitigationRef(roleUncertain)))
                : RuleEvaluationOutcome.NotTriggered()
        };

        return outcomes;
    }

    /// <summary>Evidence link back to the exact Litigation rows a finding is about — the Litigation tab
    /// reads this to attribute a per-case role ("Filed Against" / "Role not determined"); a case named by
    /// no finding shows "Role not determined". Shape matches AnalysisFinding.SourceReferenceJson.</summary>
    private static string LitigationRef(IEnumerable<Litigation> cases) => JsonSerializer.Serialize(new
    {
        entityType = nameof(Litigation),
        entityIds = cases.Select(l => l.LitigationId).OrderBy(id => id).ToArray()
    });

    private static bool IsPending(string? caseStatus)
    {
        if (string.IsNullOrWhiteSpace(caseStatus)) return false; // unknown status — excluded rather than assumed pending
        var text = caseStatus.ToLowerInvariant();
        return !ClosedKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));
    }

    private enum LitigantRole { ByCompany, AgainstCompany, Unknown }

    private static LitigantRole DetermineRole(string? litigants, string? companyName)
    {
        if (string.IsNullOrWhiteSpace(litigants) || string.IsNullOrWhiteSpace(companyName))
            return LitigantRole.Unknown;

        var parts = VsSplitRegex().Split(litigants);
        if (parts.Length != 2)
            return LitigantRole.Unknown;

        var normalizedCompany = companyName.Trim().ToLowerInvariant();
        var first = parts[0].Trim().ToLowerInvariant();
        var second = parts[1].Trim().ToLowerInvariant();
        var inFirst = first.Contains(normalizedCompany, StringComparison.Ordinal);
        var inSecond = second.Contains(normalizedCompany, StringComparison.Ordinal);

        if (inFirst && !inSecond) return LitigantRole.ByCompany;
        if (inSecond && !inFirst) return LitigantRole.AgainstCompany;
        return LitigantRole.Unknown;
    }

    [GeneratedRegex(@"\bvs\.?\b|\bv\.\b", RegexOptions.IgnoreCase)]
    private static partial Regex VsSplitRegex();
}
