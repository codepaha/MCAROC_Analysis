using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

public record ChargeSecurityComponentDraft(
    SecurityType SecurityType, ChargeRanking Ranking, string? AssetPhrase, bool? IsPrimary,
    ChargeClassificationConfidence Confidence, string MatchedRule);

public record MatchedRuleEntry(string Attribute, string Value, string MatchedText, string Rule);

public record ChargeSecurityClassification(
    IReadOnlyList<FacilityType> FacilityTypes,
    FacilityType? PrimaryFacilityType,
    IReadOnlyList<ChargeSecurityComponentDraft> SecurityComponents,
    ChargeArrangement Arrangement,
    bool IsConsortium,
    bool IsJointCharge,
    ChargeClassificationConfidence OverallConfidence,
    string MatchedRulesJson)
{
    public static readonly ChargeSecurityClassification None =
        new([], null, [], ChargeArrangement.Unknown, false, false, ChargeClassificationConfidence.None, "[]");
}

/// <summary>Deterministic keyword/phrase normalizer over a charge event's security narrative (same style
/// as FilingClassifier — ordered rules, confidence tier, matched-rule trail, no I/O). It never guesses:
/// unrecognised text yields <see cref="ChargeSecurityClassification.None"/> and the UI then shows the raw
/// wording only. Ranking is per security component (a charge can be pari-passu over current assets AND a
/// first charge over movable assets); Arrangement (consortium / joint) is tracked separately.</summary>
public static class ChargeSecurityClassifier
{
    private static readonly (string Kw, FacilityType Type)[] FacilityRules =
    [
        ("bank guarantee", FacilityType.BankGuarantee), ("bg/", FacilityType.BankGuarantee),
        (" bg ", FacilityType.BankGuarantee), ("bg limit", FacilityType.BankGuarantee),
        ("letter of credit", FacilityType.LetterOfCredit), ("ilc", FacilityType.LetterOfCredit),
        ("/lc", FacilityType.LetterOfCredit), ("lc/", FacilityType.LetterOfCredit), (" lc ", FacilityType.LetterOfCredit),
        ("buyer's credit", FacilityType.BuyersCredit), ("buyers credit", FacilityType.BuyersCredit),
        ("cash credit", FacilityType.CashCredit), (" cc ", FacilityType.CashCredit), ("cc rs", FacilityType.CashCredit),
        ("corporate term loan", FacilityType.TermLoan), ("term loan", FacilityType.TermLoan),
        ("working capital", FacilityType.WorkingCapital),
    ];

    private static readonly (string Kw, SecurityType Type)[] SecurityRules =
    [
        ("current assets", SecurityType.CurrentAssets), ("current asset", SecurityType.CurrentAssets),
        ("stock", SecurityType.CurrentAssets), ("inventory", SecurityType.CurrentAssets),
        ("book debt", SecurityType.BookDebts), ("receivable", SecurityType.BookDebts),
        ("immovable property", SecurityType.ImmovableProperty), ("immoveable property", SecurityType.ImmovableProperty),
        ("land and building", SecurityType.ImmovableProperty), ("factory", SecurityType.ImmovableProperty),
        ("movable fixed asset", SecurityType.MovableFixedAssets), ("moveable fixed asset", SecurityType.MovableFixedAssets),
        ("plant and machinery", SecurityType.MovableFixedAssets), ("plant & machinery", SecurityType.MovableFixedAssets),
        ("movable property", SecurityType.MovableFixedAssets), ("moveable property", SecurityType.MovableFixedAssets),
        ("fixed deposit", SecurityType.FixedDeposit), ("deposit lien", SecurityType.FixedDeposit),
        ("hypothecation of vehicle", SecurityType.Vehicle), ("vehicle", SecurityType.Vehicle),
    ];

    private static readonly (string Kw, ChargeRanking Rank)[] RankingRules =
    [
        ("pari passu", ChargeRanking.PariPassu), ("pari-passu", ChargeRanking.PariPassu), ("paripassu", ChargeRanking.PariPassu),
        ("exclusive charge", ChargeRanking.Exclusive), ("exclusive first", ChargeRanking.Exclusive),
        ("second charge", ChargeRanking.SecondCharge), ("2nd charge", ChargeRanking.SecondCharge),
        ("subservient", ChargeRanking.Subservient), ("subordinate", ChargeRanking.Subservient),
        ("first charge", ChargeRanking.FirstCharge), ("1st charge", ChargeRanking.FirstCharge),
    ];

    public static ChargeSecurityClassification Classify(
        string? instrumentDescription, string? propertyType, string? propertyParticulars,
        string? extentAndOperation, string? otherTerms, bool? jointHolding, bool? consortiumHolding)
    {
        // The narrative (clause-splittable) is kept separate from PropertyType (a flat comma-separated
        // type list with no ranking language).
        var narrative = Join(propertyParticulars, extentAndOperation, otherTerms).ToLowerInvariant();
        var propertyTypeText = (propertyType ?? "").ToLowerInvariant();
        var securityText = (narrative + " " + propertyTypeText).Trim();
        var facilityText = Join(instrumentDescription, otherTerms, extentAndOperation).ToLowerInvariant();
        var allText = securityText + " " + facilityText;

        if (string.IsNullOrWhiteSpace(allText.Replace("-", "").Replace(",", "").Trim()))
            return ChargeSecurityClassification.None;

        var matched = new List<MatchedRuleEntry>();

        // Facilities.
        var facilities = new List<FacilityType>();
        foreach (var (kw, type) in FacilityRules)
            if (facilityText.Contains(kw) && !facilities.Contains(type))
            {
                facilities.Add(type);
                matched.Add(new MatchedRuleEntry("Facility", type.ToString(), kw.Trim(), $"FAC_{type}".ToUpperInvariant()));
            }

        // Arrangement.
        var consortium = consortiumHolding == true
            || securityText.Contains("consortium") || securityText.Contains("other working capital banker")
            || securityText.Contains("multiple banking");
        var joint = jointHolding == true || allText.Contains("joint charge") || allText.Contains("joint holder");
        if (consortium) matched.Add(new MatchedRuleEntry("Arrangement", "Consortium",
            consortiumHolding == true ? "consortium flag" : "consortium wording", "ARR_CONSORTIUM"));
        if (joint) matched.Add(new MatchedRuleEntry("Arrangement", "JointCharge",
            jointHolding == true ? "joint flag" : "joint wording", "ARR_JOINT"));
        var arrangement = (consortium, joint) switch
        {
            (true, true) => ChargeArrangement.MultipleLenders,
            (true, false) => ChargeArrangement.Consortium,
            (false, true) => ChargeArrangement.JointCharge,
            _ => securityText.Contains("exclusive") ? ChargeArrangement.Sole : ChargeArrangement.Unknown
        };

        // Security components — split the NARRATIVE (not PropertyType) into clauses so "pari passu over
        // current assets and first charge over movable assets" produces two rows with different rankings.
        var clauses = narrative.Split([" and ", ";", ". "], StringSplitOptions.RemoveEmptyEntries);
        var narrativeDominantRank = FirstMatch(RankingRules, narrative);
        var isPrimaryOverall = allText.Contains("primary security") ? true
            : allText.Contains("collateral security") || allText.Contains("additional security") ? (bool?)false
            : null;

        var components = new List<ChargeSecurityComponentDraft>();
        void AddComponent(SecurityType secType, ChargeRanking rank, string? phrase, bool? isPrimary)
        {
            if (components.Any(x => x.SecurityType == secType)) return;
            var conf = rank != ChargeRanking.Unknown ? ChargeClassificationConfidence.High : ChargeClassificationConfidence.Medium;
            components.Add(new ChargeSecurityComponentDraft(secType, rank, phrase, isPrimary, conf, $"SEC_{secType}".ToUpperInvariant()));
            matched.Add(new MatchedRuleEntry("Security", secType.ToString(), phrase ?? "", $"SEC_{secType}".ToUpperInvariant()));
            if (rank != ChargeRanking.Unknown)
                matched.Add(new MatchedRuleEntry("Ranking", rank.ToString(), RankingKeyword(rank), $"RANK_{rank}".ToUpperInvariant()));
        }

        foreach (var clause in clauses)
        {
            var clauseRank = FirstMatch(RankingRules, clause) ?? narrativeDominantRank ?? ChargeRanking.Unknown;
            bool? isPrimary = clause.Contains("primary security") ? true
                : clause.Contains("collateral security") || clause.Contains("additional security") ? false
                : isPrimaryOverall;
            foreach (var secType in MatchSecurityTypes(clause))
                AddComponent(secType, clauseRank, Trim(clause), isPrimary);
        }

        // PropertyType is a bare type list — add any type it names that the narrative didn't, at the
        // narrative's dominant ranking (or Unknown).
        foreach (var secType in MatchSecurityTypes(propertyTypeText))
            AddComponent(secType, narrativeDominantRank ?? ChargeRanking.Unknown, null, isPrimaryOverall);

        var overall = OverallConfidence(components, facilities, arrangement);
        return new ChargeSecurityClassification(
            facilities, facilities.Count > 0 ? facilities[0] : null,
            components, arrangement, consortium, joint, overall,
            JsonSerializer.Serialize(matched));
    }

    private static ChargeRanking? FirstMatch((string Kw, ChargeRanking Rank)[] rules, string text)
    {
        foreach (var (kw, rank) in rules)
            if (text.Contains(kw)) return rank;
        return null;
    }

    /// <summary>Security types named in a piece of text. "immovable property" contains the substring
    /// "movable property", so once immovable is matched, movable rules are suppressed for that text.</summary>
    private static IEnumerable<SecurityType> MatchSecurityTypes(string text)
    {
        var immovable = text.Contains("immovable") || text.Contains("immoveable");
        var seen = new HashSet<SecurityType>();
        foreach (var (kw, secType) in SecurityRules)
        {
            if (secType == SecurityType.MovableFixedAssets && immovable && (kw.Contains("movable") && !kw.Contains("immovable")))
            {
                // only skip the generic "movable ..." keywords, not an explicit "movable fixed asset"
                if (!text.Contains("movable fixed asset") && !text.Contains("moveable fixed asset")) continue;
            }
            if (text.Contains(kw) && seen.Add(secType))
                yield return secType;
        }
    }

    private static string RankingKeyword(ChargeRanking r) => RankingRules.First(x => x.Rank == r).Kw;

    private static ChargeClassificationConfidence OverallConfidence(
        IReadOnlyList<ChargeSecurityComponentDraft> components, IReadOnlyList<FacilityType> facilities, ChargeArrangement arrangement)
    {
        if (components.Count == 0 && facilities.Count == 0 && arrangement == ChargeArrangement.Unknown)
            return ChargeClassificationConfidence.None;
        if (components.Any(c => c.Ranking != ChargeRanking.Unknown))
            return ChargeClassificationConfidence.High;
        if (components.Count > 0)
            return ChargeClassificationConfidence.Medium;
        return ChargeClassificationConfidence.Low;
    }

    private static string Join(params string?[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p) && p!.Trim() != "-"));

    private static string? Trim(string s) => s.Trim() is { Length: > 0 } t ? (t.Length > 200 ? t[..200] : t) : null;
}
