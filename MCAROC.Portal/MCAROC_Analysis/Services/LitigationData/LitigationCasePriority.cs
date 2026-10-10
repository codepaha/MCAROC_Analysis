using MCAROC_Analysis.Models;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>How worthwhile a full AI reading of a case is. A: read it in full. B: the cheap per-order outcome classification is enough.
/// C: the court record alone.</summary>
public enum LitigationAnalysisPriority { C, B, A }

public sealed record LitigationCasePriorityResult(int Score, LitigationAnalysisPriority Priority, string Reasons);

/// <summary>Ranks cases by what a lender stands to learn from reading their orders, so a company with thousands of cases (a bank, an NBFC)
/// spends its AI budget where it matters. It is free, deterministic and only used when a company has more cases than
/// <c>LitigationAiAnalysis:TieringAboveCases</c>; a smaller company has every case read as before.
/// <para>What counts: the company as respondent, accused or defendant weighs far more than the company as claimant (a bank's own recovery
/// suits are low priority); a financial act or a fraud / criminal marker adds weight; a disposed case adds a little, because the final
/// judgment and its outcome are in it. Score = side + act + forum + criminal + disposed; A at 50 or more, B at 30 or more. An insolvency proceeding adds 200, so it is always A and always first.</para></summary>
public static partial class LitigationCasePriority
{
    public const int AThreshold = 50, BThreshold = 30;
    private const int AgainstBase = 40, ByCompanyBase = 10, InsolvencyWeight = 200;

    public static LitigationCasePriorityResult Evaluate(LitigationRiskInput c)
    {
        var reasons = new List<string>();
        var byCompany = c.Side == LitigationCompanySide.ByCompany;
        var score = byCompany ? ByCompanyBase : AgainstBase;
        reasons.Add(byCompany ? "filed by the company" : c.Side == LitigationCompanySide.Unknown ? "side unstated (treated as against)" : "against the company");

        var text = string.Join(" ", new[] { c.CaseType, c.Act, c.CaseStage, c.CaseClassification, c.ProceedingType, c.Court }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
        var forum = LitigationBaselineRisk.ForumOf(c.Type, c.Court, c.CourtCategory);

        // Insolvency (IBC, a corporate debtor, a winding-up) is pinned to the top: it puts the company's existence or a borrower's repayment at
        // stake, so it is read first and never cut by the ceiling, whichever side filed and whatever the status.
        var normalised = NonWord().Replace(text, " ");
        var insolvency = Insolvency.IsMatch(normalised);
        if (insolvency)
        {
            score += InsolvencyWeight;
            reasons.Add($"insolvency proceeding - highest priority (+{InsolvencyWeight})");
        }

        // A company's own financial-act filings still rank above its other suits, but at half weight.
        var act = ActWeight(text, out var actName);
        if (act > 0)
        {
            var applied = byCompany ? act / 2 : act;
            score += applied;
            reasons.Add($"{actName} (+{applied})");
        }

        var forumWeight = forum switch
        {
            LitigationForum.Drt or LitigationForum.Drat or LitigationForum.Nclt or LitigationForum.Nclat
                or LitigationForum.SupremeCourt or LitigationForum.HighCourt => 10,
            LitigationForum.Itat or LitigationForum.Cestat => 15,
            LitigationForum.Consumer => 5,
            _ => 0
        };
        if (forumWeight > 0) { score += forumWeight; reasons.Add($"{forum} (+{forumWeight})"); }

        if (!byCompany && Criminal().IsMatch(text)) { score += 15; reasons.Add("criminal or fraud proceeding (+15)"); }

        if (c.Status == LitigationCaseStatusBucket.Disposed)
        {
            var bump = byCompany ? 5 : 10;
            score += bump;
            reasons.Add($"disposed - final order on file (+{bump})");
        }

        var priority = score >= AThreshold ? LitigationAnalysisPriority.A : score >= BThreshold ? LitigationAnalysisPriority.B : LitigationAnalysisPriority.C;
        return new(score, priority, string.Join("; ", reasons));
    }

    /// <summary>The weight of the heaviest financial act the case text names, and which one it was.</summary>
    internal static int ActWeight(string lowerText, out string name)
    {
        var text = NonWord().Replace(lowerText, " ");   // "S.138 N.I. Act" -> "s 138 n i act"
        foreach (var (label, weight, pattern) in Acts)
            if (pattern.IsMatch(text)) { name = label; return weight; }
        if (CheatingSections().IsMatch(text)) { name = "cheating / forgery / breach of trust"; return 20; }
        name = "";
        return 0;
    }

    private static Regex P(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Heaviest first. 35 = acts that go to a lender's own security, default or money trail; 20 = other acts that bear on credit standing;
    // 10 = security documents. Matched on words, so a short name such as "ibc" never hits inside another word.
    private static readonly (string Label, int Weight, Regex Pattern)[] Acts =
    [
        ("SARFAESI", 35, P(@"\bsarfaesi\b|\bsecuriti[sz]ation\b")),
        ("recovery of debts / DRT", 35, P(@"\brecovery of debts\b|\bdebts? due to banks\b|\brdb\b|\brdbfi\b")),
        ("cheque dishonour (NI Act s.138)", 35, P(@"\bnegotiable instruments?\b|\bn i act\b|\bni act\b|\bs(ection)? ?138\b|\bcheque (bounce|dishonou?r)\b")),
        ("money laundering (PMLA)", 35, P(@"\bpmla\b|\bmoney laundering\b")),
        ("foreign exchange (FEMA)", 35, P(@"\bfema\b|\bforeign exchange management\b")),
        ("prevention of corruption", 35, P(@"\bprevention of corruption\b")),
        ("banking regulation / RBI Act", 35, P(@"\bbanking regulation\b|\breserve bank of india act\b|\brbi act\b")),
        ("wilful defaulter / fugitive offender", 35, P(@"\bwil+ful defaulter\b|\bfugitive economic\b")),
        ("Companies Act (oppression / fraud)", 20, P(@"\bcompanies act\b|\boppression\b|\bmismanagement\b|\bsection 447\b|\bs(ection)? ?24[12]\b")),
        ("securities law (SEBI)", 20, P(@"\bsebi\b|\bsecurities and exchange\b|\bsecurities contracts\b")),
        ("arbitration award", 20, P(@"\barbitration\b|\barbitral award\b")),
        ("consumer protection (deficiency of service)", 20, P(@"\bconsumer protection\b|\bdeficiency in service\b")),
        ("cyber / IT Act", 20, P(@"\binformation technology act\b|\bcyber ?crime\b|\bit act\b")),
        ("tax (income tax / GST / customs)", 20, P(@"\bincome tax\b|\bgst\b|\bcustoms act\b|\bcentral excise\b")),
        ("guarantee / mortgage / hypothecation", 10, P(@"\bguarantee\b|\bmortgage\b|\bhypothecation\b")),
    ];

    [GeneratedRegex(@"\b(ipc|bns|indian penal)\b.{0,40}?\b(420|406|409|467|468|471|120 ?b|318|316|319)\b|\bcheating\b|\bforgery\b|\bbreach of trust\b|\bmisappropriation\b", RegexOptions.IgnoreCase)]
    private static partial Regex CheatingSections();

    [GeneratedRegex(@"criminal|bail|crpc|cr\.p\.c|\bipc\b|\bbns\b|\bfir\b|charge ?sheet|complaint case|crl\.|cheating|forgery|breach of trust|misappropriation|fraud", RegexOptions.IgnoreCase)]
    private static partial Regex Criminal();

    // IBC, a corporate debtor, a winding-up petition: pinned to the top of the ranking.
    private static readonly Regex Insolvency = P(@"\bibc\b|\binsolvency\b|(?<!debts and )\bbankruptcy\b|\bcorporate (debtor|insolvency)\b|\bcpib\b|\bwinding up\b");

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonWord();
}
