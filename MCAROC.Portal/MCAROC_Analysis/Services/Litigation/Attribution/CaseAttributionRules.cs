using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;
using MCAROC_Analysis.Services.LitigationModule.Refresh;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

/// <summary>What the rules alone say about one case. <see cref="Candidates"/> are the party names to judge when the rules cannot settle it, most likely first;
/// <see cref="Truncated"/> says some possible parties were left out by the cap, so "none of the judged parties is the company" does not settle the case.</summary>
public sealed record RuleAssessment(LitigationAttributionOutcome? Outcome, string? Rule, string? Party, string? Reason, IReadOnlyList<string> Candidates,
    bool Truncated = false, int PossibleCount = 0)
{
    public bool NeedsJudgement => Outcome is null;
}

/// <summary>
/// The deterministic part of attribution (#489, decision a), always applied before any judgement and never overridden by one:
/// <list type="number">
/// <item>a party that is the company by the whole-name rule (current or former name, or an office acting for it) makes the case the company's;</item>
/// <item>when every party that shares a word with the company's names is another body named after it (its employees' union, a cooperative society, a
/// trust...) and no party is written in Devanagari, the case is not the company's;</item>
/// <item>otherwise the parties that share a word (or a sound) with its names are judged; when none does, which happens for a case found through a Hindi or
/// phonetic keyword, all its parties are.</item>
/// </list>
/// </summary>
public static class CaseAttributionRules
{
    public const int MaxCandidatesPerCase = 12;

    /// <summary>Words that make a party a different body from the company it is named after.</summary>
    private static readonly HashSet<string> OtherBodyWords = new(StringComparer.Ordinal)
    {
        "EMPLOYEES", "EMPLOYEE", "STAFF", "WORKERS", "WORKMEN", "UNION", "COOPERATIVE", "OPERATIVE", "SOCIETY", "TRUST", "FOUNDATION", "ASSOCIATION",
        "FEDERATION", "CLUB", "CREDIT", "THRIFT", "WELFARE", "PROVIDENT", "PENSION", "GRATUITY", "SUPERANNUATION", "SCHOOL", "COLLEGE", "HOSPITAL"
    };

    private static readonly HashSet<string> NotDistinctive = new(StringComparer.Ordinal)
    {
        "LIMITED", "PRIVATE", "LLP", "PUBLIC", "COMPANY", "CO", "CORPORATION", "CORP", "INC", "AND", "THE", "OF", "INDIA", "INDIAN", "NEW", "M", "S", "MS"
    };

    public static RuleAssessment Assess(IReadOnlyList<string> parties, IReadOnlyCollection<string> currentNames, IReadOnlyCollection<string> formerNames)
    {
        if (parties.Count == 0)
            return new(LitigationAttributionOutcome.Ambiguous, "no-parties", null, "The record lists no parties, so it cannot be told whose case it is.", []);

        foreach (var party in parties)
        {
            var match = WholeNamePartyMatcher.Match(party, currentNames, formerNames);
            if (match.Outcome == PartyMatchOutcome.Verified)
                return new(LitigationAttributionOutcome.Attributed, match.Rule, party, ReasonFor(match), []);
        }

        var companyWords = currentNames.Concat(formerNames).SelectMany(DistinctiveWords).ToHashSet(StringComparer.Ordinal);
        var companySounds = companyWords.Select(NamePhoneticKey.WordKey).Where(k => k.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var resembling = parties.Where(p => Resembles(p, companyWords, companySounds)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // A word of the company's own name never makes a party another body ("ABC Credit Limited" is not a credit society).
        bool IsOtherBody(string party) => WholeNamePartyMatcher.Canonical(party).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(w => OtherBodyWords.Contains(w) && !companyWords.Contains(w));
        // Settled without a judgement only when no party is left unassessed: a Devanagari party (the company's Hindi name shares no Latin word with it)
        // could be the company itself, beside its employees' society (review of #500).
        var devanagari = parties.Where(NamePhoneticKey.IsDevanagari).Distinct(StringComparer.Ordinal).ToList();
        if (resembling.Count > 0 && resembling.All(IsOtherBody) && devanagari.Count == 0)
            return new(LitigationAttributionOutcome.NotTheCompany, "other-body", resembling[0],
                "The party named after the company is another body (its employees, a society, a trust...), not the company.", []);

        // The parties that could be the company: those resembling it (most shared words first), and any written in Devanagari (its Hindi name shares no
        // Latin word with it); when none resembles it, every party. Capped; a case cut short by the cap is never settled as "not the company's" (review of #500).
        var possible = (resembling.Count > 0
                ? resembling.Where(p => !IsOtherBody(p)).OrderByDescending(p => SharedWords(p, companyWords, companySounds))
                    .Concat(devanagari)
                : parties)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(null, null, null, null, possible.Take(MaxCandidatesPerCase).ToList(), possible.Count > MaxCandidatesPerCase, possible.Count);
    }

    private static string ReasonFor(PartyMatchResult match) => match.Rule switch
    {
        "exact" => "A party is the company's registered name.",
        "exact-former-name" => $"A party is the company's former name ({match.MatchedName}).",
        "exact-representative" => "A party is the company, named through the office acting for it.",
        "exact-former-name-representative" => $"A party is the company's former name ({match.MatchedName}), named through the office acting for it.",
        _ => "A party is the company."
    };

    internal static IEnumerable<string> DistinctiveWords(string? name) =>
        WholeNamePartyMatcher.Canonical(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !NotDistinctive.Contains(w));

    private static int SharedWords(string party, HashSet<string> companyWords, HashSet<string> companySounds) =>
        DistinctiveWords(party).Distinct().Count(w => companyWords.Contains(w) || companySounds.Contains(NamePhoneticKey.WordKey(w)));

    private static bool Resembles(string party, HashSet<string> companyWords, HashSet<string> companySounds)
    {
        if (NamePhoneticKey.IsDevanagari(party)) return false;   // judged only when nothing in Latin resembles the company
        foreach (var word in DistinctiveWords(party))
            if (companyWords.Contains(word) || companySounds.Contains(NamePhoneticKey.WordKey(word))) return true;
        return false;
    }

}
