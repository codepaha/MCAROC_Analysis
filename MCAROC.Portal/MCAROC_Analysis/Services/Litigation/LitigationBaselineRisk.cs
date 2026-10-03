using System.Text.RegularExpressions;
using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>A case's risk tier, from R3 (critical) to R0 (no litigation). The same four tiers the owner's Case Risk Analysis module uses.</summary>
public enum LitigationRiskTier { R0, R1, R2, R3 }

/// <summary>Which side of the case the company is on, worked out from the party names (the provider's own direction is empty for
/// most records).</summary>
public enum LitigationCompanySide { Unknown, ByCompany, AgainstCompany, Both }

/// <summary>A tier with the trigger that set it and a plain-language reason. <see cref="Basis"/> says what it was worked out from.</summary>
public sealed record LitigationRiskAssessment(LitigationRiskTier Tier, string Trigger, string Reason, string Basis = "Court record");

/// <summary>The fields of a litigation case the baseline tier is read from.</summary>
public sealed record LitigationRiskInput(
    string? Type, string? Court, string? CourtCategory, string? CaseType, string? Act, string? CaseStage, string? CaseClassification,
    string? ProceedingType, LitigationCaseStatusBucket Status, LitigationCompanySide Side, IReadOnlyList<string> Petitioners);

/// <summary>The forum a case sits in.</summary>
public enum LitigationForum { SupremeCourt, HighCourt, DistrictCourt, Nclt, Nclat, Drt, Drat, Consumer, Itat, Cestat, Rera, Other }

/// <summary>#step 1 of the litigation case page: a baseline R0–R3 tier worked out from the case's own court record — forum, case type,
/// act, status and the company's side — by the owner's Case Risk Analysis trigger table for a bank lender. It is free and
/// reproducible. It is deliberately conservative where the record is silent (an unknown status is treated as pending, an unknown side
/// as against the company), and it is a baseline: when the orders have been analysed they can confirm or raise it.
/// Triggers are evaluated in strict order and the first match decides.</summary>
public static partial class LitigationBaselineRisk
{
    public static LitigationForum ForumOf(string? type, string? court, string? courtCategory)
    {
        var t = (type ?? "").Trim().ToLowerInvariant();
        switch (t)
        {
            case "drt": return LitigationForum.Drt;
            case "drat": return LitigationForum.Drat;
            case "nclt": return LitigationForum.Nclt;
            case "nclat": return LitigationForum.Nclat;
            case "cestat": return LitigationForum.Cestat;
            case "itat": return LitigationForum.Itat;
            case "rera": return LitigationForum.Rera;
            case "consumer": return LitigationForum.Consumer;
            case "supreme" or "supremecourt" or "supreme_court": return LitigationForum.SupremeCourt;
            case "highcourt" or "high_court" or "high": return LitigationForum.HighCourt;
            case "district" or "districtcourt" or "district_court": return LitigationForum.DistrictCourt;
        }
        var c = (court ?? "").ToLowerInvariant();
        if (c.Length == 0) return LitigationForum.Other;
        if (c.Contains("debts recovery appellate") || c.Contains("debt recovery appellate") || c.Contains("drat")) return LitigationForum.Drat;
        if (c.Contains("debts recovery") || c.Contains("debt recovery") || c.Contains("drt")) return LitigationForum.Drt;
        if (c.Contains("nclat") || (c.Contains("appellate tribunal") && c.Contains("company law"))) return LitigationForum.Nclat;
        if (c.Contains("nclt") || c.Contains("national company law tribunal")) return LitigationForum.Nclt;
        if (c.Contains("cestat") || c.Contains("customs, excise")) return LitigationForum.Cestat;
        if (c.Contains("itat") || c.Contains("income tax appellate")) return LitigationForum.Itat;
        if (c.Contains("supreme court")) return LitigationForum.SupremeCourt;
        if (c.Contains("high court")) return LitigationForum.HighCourt;
        if (c.Contains("consumer")) return LitigationForum.Consumer;
        if (c.Contains("rera") || c.Contains("real estate regulatory")) return LitigationForum.Rera;
        if (c.Contains("district") || c.Contains("sessions") || c.Contains("civil judge") || c.Contains("magistrate") || c.Contains("munsif")) return LitigationForum.DistrictCourt;
        return LitigationForum.Other;
    }

    public static LitigationRiskAssessment Assess(LitigationRiskInput c)
    {
        var forum = ForumOf(c.Type, c.Court, c.CourtCategory);
        var text = string.Join(" ", new[] { c.CaseType, c.Act, c.CaseStage, c.CaseClassification, c.ProceedingType }
            .Where(s => !string.IsNullOrWhiteSpace(s))).ToLowerInvariant();
        var compact = NonAlphanumeric().Replace(text, "");
        var pendingLike = c.Status != LitigationCaseStatusBucket.Disposed; // an unknown status is treated as pending
        var againstOrUnknown = c.Side != LitigationCompanySide.ByCompany;

        bool Has(params string[] words) => words.Any(w => text.Contains(w, StringComparison.Ordinal));
        bool HasCompact(params string[] words) => words.Any(w => compact.Contains(w, StringComparison.Ordinal));

        var insolvency = HasCompact("ibc", "cpib", "insolvenc", "corporatedebtor", "corporateinsolvency");
        var windingUp = HasCompact("windingup", "companypetition", "transferpetitioncompaniesact")
            || (HasCompact("companiesact") && HasCompact("433", "434", "439"));
        var criminal = Has("criminal", "bail", "crpc", "cr.p.c", "ipc", "bns", "fir ", "charge sheet", "chargesheet", "complaint case", "crl.") || HasCompact("crlmp", "crlrc", "crlp");

        // ── R3: critical ──
        if (forum is LitigationForum.Drt or LitigationForum.Drat)
            return new(LitigationRiskTier.R3, "DRT/DRAT recovery proceeding",
                "A debt-recovery tribunal proceeding is critical at any status: the default or enforcement history stays material even when disposed.");
        if (forum is LitigationForum.Nclt or LitigationForum.Nclat && insolvency)
            return new(LitigationRiskTier.R3, "NCLT/NCLAT insolvency proceeding",
                "An insolvency proceeding puts the company's corporate existence at stake and discloses its default history even when disposed or withdrawn.");
        if (windingUp)
            return new(LitigationRiskTier.R3, "Company / winding-up petition",
                "A company or winding-up petition is treated as an insolvency matter at any status.");
        if (forum is LitigationForum.Itat or LitigationForum.Cestat)
            return new(LitigationRiskTier.R3, "Tax appeal (ITAT / CESTAT)", "A tax tribunal appeal against the company is critical at any status.");
        if (Has("sarfaesi", "securitisation", "securitization")) return new(LitigationRiskTier.R3, "SARFAESI action", "A SARFAESI proceeding is critical at any status.");
        if (HasCompact("wilfuldefaulter")) return new(LitigationRiskTier.R3, "Wilful defaulter", "A wilful-defaulter matter is critical at any status.");
        if (HasCompact("pocso") || Has("domestic violence")) return new(LitigationRiskTier.R3, "POCSO / domestic violence proceeding", "Critical at any status.");
        if (againstOrUnknown && (Has("negotiable instrument", "n.i. act", "ni act") || HasCompact("section138", "s138", "ni138") || (Has("cheque") && Has("138"))))
            return new(LitigationRiskTier.R3, "NI Act s.138 (cheque bounce)", "A cheque-bounce prosecution against the company is critical at any status.");
        if (Has("cbi") && criminal) return new(LitigationRiskTier.R3, "CBI case", "A CBI case is critical at any status.");
        if (pendingLike && Has("recovery suit", "money suit", "money recovery", "possession suit", "execution petition", "execution application", "darkhast"))
            return new(LitigationRiskTier.R3, "Pending recovery / execution proceeding",
                "A pending recovery suit or execution proceeding is critical while it is pending; once disposed it falls to a lower tier.");
        if (pendingLike && criminal && againstOrUnknown)
            return new(LitigationRiskTier.R3, "Pending criminal case (company as accused or side unstated)",
                "A pending criminal case puts the company's management or the company itself at risk; a side the record does not state is treated as against the company.");

        // ── R2: high ──
        if (criminal) return new(LitigationRiskTier.R2, "Criminal case — historical or non-accused exposure", "A criminal case stays a material disclosure item even when disposed.");
        if (Has("prevention of corruption", "benami", "fema", "foreign exchange management", "black money", "enforcement directorate"))
            return new(LitigationRiskTier.R2, "Financial-credibility statute", "An act with a direct bearing on financial credibility or collateral enforceability.");
        if (forum is LitigationForum.Nclt or LitigationForum.Nclat)
            return new(LitigationRiskTier.R2, "NCLT/NCLAT matter — not stated as insolvency",
                "A company-law tribunal proceeding is treated as at least high risk until its orders show otherwise.");
        if (pendingLike && c.Petitioners.Any(IsFinancialInstitution))
            return new(LitigationRiskTier.R2, "Financial institution as petitioner (pending)", "A bank or finance company is the petitioner in a pending matter.");
        if (pendingLike && Has("first appeal", "civil appeal", "second appeal", "regular appeal") && Has("title", "partition", "specific performance", "possession"))
            return new(LitigationRiskTier.R2, "Pending appeal in a title / possession dispute", "An appeal continuing a property dispute.");

        // ── R1: low ──
        return pendingLike
            ? new(LitigationRiskTier.R1, "Pending civil or administrative matter", "Pending, with no higher-tier trigger on the court record.")
            : new(LitigationRiskTier.R1, "Disposed matter — no higher-tier trigger on the court record",
                "Disposed, with no financial-default, insolvency or criminal trigger stated on the court record.");
    }

    /// <summary>The tier of a whole report: the highest of its cases; R0 when there are none.</summary>
    public static LitigationRiskTier PortfolioTier(IEnumerable<LitigationRiskTier> caseTiers)
    {
        var tier = LitigationRiskTier.R0;
        foreach (var t in caseTiers) if (t > tier) tier = t;
        return tier;
    }

    public static string Label(LitigationRiskTier tier) => tier switch
    {
        LitigationRiskTier.R3 => "Critical risk",
        LitigationRiskTier.R2 => "High risk",
        LitigationRiskTier.R1 => "Low risk",
        _ => "No risk"
    };

    public static string BadgeCss(LitigationRiskTier tier) => tier switch
    {
        LitigationRiskTier.R3 => "bg-danger text-white",
        LitigationRiskTier.R2 => "bg-warning text-dark",
        LitigationRiskTier.R1 => "bg-info text-dark",
        _ => "bg-success text-white"
    };

    private static bool IsFinancialInstitution(string name) =>
        FinancialInstitution().IsMatch(name);

    [GeneratedRegex(@"\b(bank|finance|financial|finserv|nbfc|housing finance|capital|credit|asset reconstruction|arcil|leasing|microfin)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinancialInstitution();

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphanumeric();
}

/// <summary>The tiers of a whole report: how many cases sit at each tier, the overall tier (the highest), and what set the top tiers.</summary>
public sealed class LitigationRiskProfile
{
    public Dictionary<LitigationRiskTier, int> Cases { get; } = Enum.GetValues<LitigationRiskTier>().ToDictionary(t => t, _ => 0);
    private readonly Dictionary<(LitigationRiskTier Tier, string Trigger), int> _triggers = [];

    public int TotalCases => Cases.Values.Sum();
    public LitigationRiskTier Overall => TotalCases == 0 ? LitigationRiskTier.R0 : Cases.Where(kv => kv.Value > 0).Max(kv => kv.Key);

    public void Add(LitigationRiskAssessment assessment)
    {
        Cases[assessment.Tier]++;
        _triggers[(assessment.Tier, assessment.Trigger)] = _triggers.GetValueOrDefault((assessment.Tier, assessment.Trigger)) + 1;
    }

    /// <summary>What set the cases of a tier, most common first: "NCLT/NCLAT insolvency proceeding" × 43.</summary>
    public IReadOnlyList<(string Trigger, int Count)> TriggersOf(LitigationRiskTier tier) =>
        _triggers.Where(kv => kv.Key.Tier == tier).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Trigger, StringComparer.Ordinal)
            .Select(kv => (kv.Key.Trigger, kv.Value)).ToList();
}

/// <summary>Which side of a case the company is on, from the petitioner and respondent names. The provider's own direction is empty on
/// most records, so it is worked out here: a party name that is the company's name — current or earlier — marks that side.</summary>
public static partial class LitigationCompanySides
{
    private static readonly string[] SuffixWords = ["LIMITED", "LTD", "PRIVATE", "PVT", "LLP", "COMPANY", "CO", "INDIA", "THE", "M", "MS", "OF", "AND"];

    /// <summary>A name reduced to its distinguishing words: upper case, no punctuation, no "Ltd / Pvt / M/s" and the like.</summary>
    public static string Core(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var words = NonAlnum().Replace(MessrsPrefix().Replace(name.ToUpperInvariant(), " ").Replace("&", " AND "), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !SuffixWords.Contains(w));
        return string.Join(' ', words);
    }

    public static bool IsCompany(string? partyName, IReadOnlyCollection<string> companyCores)
    {
        var party = Core(partyName);
        if (party.Length == 0) return false;
        foreach (var core in companyCores)
        {
            if (core.Length == 0) continue;
            if (party == core) return true;
            // "COASTAL PROJECTS (IN LIQUIDATION)" and "COASTAL PROJECTS" are the same party; a one-word core must match whole.
            if (core.Contains(' ') && (party.StartsWith(core + " ", StringComparison.Ordinal) || party.EndsWith(" " + core, StringComparison.Ordinal))) return true;
        }
        return false;
    }

    /// <summary>True when the text names the company: its distinguishing words (current or earlier name, "Ltd / Pvt / M/s" ignored) occur in
    /// order as whole words, whatever the case or spacing.</summary>
    public static bool NamesCompany(string? text, IReadOnlyCollection<string> companyCores)
    {
        if (string.IsNullOrWhiteSpace(text) || companyCores.Count == 0) return false;
        var flat = " " + NonAlnum().Replace(MessrsPrefix().Replace(text.ToUpperInvariant(), " "), " ").Trim() + " ";
        return companyCores.Any(core => core.Length > 0 && flat.Contains(" " + core + " ", StringComparison.Ordinal));
    }

    /// <summary>The company's side from the party names — a name that is the company's own, current or earlier, marks its side. When the names
    /// do not say (the company is not among them, or the provider sent none), the provider's own direction decides: <c>by_or_against</c>
    /// says whether the case is by or against the entity that was searched.</summary>
    public static LitigationCompanySide Determine(
        IEnumerable<string> petitioners, IEnumerable<string> respondents, IEnumerable<string> companyNames, string? providerDirection = null)
    {
        var fromProvider = providerDirection?.Trim().ToLowerInvariant() switch
        {
            "by" => LitigationCompanySide.ByCompany,
            "against" => LitigationCompanySide.AgainstCompany,
            _ => LitigationCompanySide.Unknown
        };
        var cores = companyNames.Select(Core).Where(c => c.Length > 0).Distinct().ToList();
        if (cores.Count == 0) return fromProvider;
        var by = petitioners.Any(p => IsCompany(p, cores));
        var against = respondents.Any(r => IsCompany(r, cores));
        return (by, against) switch
        {
            (true, true) => LitigationCompanySide.Both,
            (true, false) => LitigationCompanySide.ByCompany,
            (false, true) => LitigationCompanySide.AgainstCompany,
            _ => fromProvider
        };
    }

    public static string Label(LitigationCompanySide side) => side switch
    {
        LitigationCompanySide.ByCompany => "Company is the petitioner",
        LitigationCompanySide.AgainstCompany => "Company is the respondent",
        LitigationCompanySide.Both => "Company appears on both sides",
        _ => "Company's side not stated"
    };

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>"M/s", "M/S." or "M.s." before a name — "Messrs", not part of it.</summary>
    [GeneratedRegex(@"\bM\s*[/.]\s*S\b\.?")]
    private static partial Regex MessrsPrefix();
}
