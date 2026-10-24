using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationData;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

public enum PersonAttributionGrade
{
    Confirmed = 1,
    Probable = 2,
    Possible = 3,
    Excluded = 4
}

public enum CorroborationStrength
{
    NotProvided = 0,
    Full = 1,
    Partial = 2,
    Conflict = 3
}

public enum DistrictMatchStrength
{
    NotProvided = 0,
    Match = 1,
    SameState = 2,
    Conflict = 3
}

public enum CompanyLinkStrength
{
    None = 0,
    Direct = 1,
    Lender = 2
}

public sealed record PersonAttributionSignals(
    CorroborationStrength FatherOrHusband,
    DistrictMatchStrength District,
    CompanyLinkStrength CompanyLink,
    bool MassParty,
    bool RelativeOnly,
    bool SingleCommonTokenOnly);

public sealed record PersonTargetProfile(
    string Name,
    string? FatherOrHusbandName = null,
    string? District = null,
    string? State = null,
    string? AssociatedCompanyName = null,
    string? Din = null,
    string? Pan = null);

public sealed record PersonRuleAssessment(
    PersonAttributionGrade Grade,
    LitigationAttributionOutcome Outcome,
    string Rule,
    string? Party,
    string Reason,
    PersonAttributionSignals Signals,
    IReadOnlyList<string> Candidates,
    bool NeedsJudgement = false);

/// <summary>
/// Corroboration and attribution rules for individuals (directors, partners, proprietors) (#452).
/// A person's name alone NEVER reaches Confirmed. Corroboration signals (father/husband, district, company link)
/// and gates (relative-only, mass-party title, single common token) determine the attribution grade:
/// - Confirmed: whole-name + positive corroboration (father/husband match or associated company co-party).
/// - Probable: whole-name + plausible secondary signal (same district, partial father, or recovery lender link).
/// - Possible: name-only match without second signal, mass party (&amp; Ors), or single common token.
/// - Excluded: relative-only appearance, corporate-only parties, conflicting father/husband, or conflicting district.
/// </summary>
public static class PersonAttributionRules
{
    public const int MaxCandidatesPerCase = 12;

    public static PersonRuleAssessment Assess(
        IReadOnlyList<string> parties,
        PersonTargetProfile target,
        string? caseDistrict = null,
        string? caseState = null,
        string? caseType = null,
        string? court = null)
    {
        if (parties.Count == 0)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Possible,
                Outcome: LitigationAttributionOutcome.Ambiguous,
                Rule: "no-parties",
                Party: null,
                Reason: "The record lists no parties, so individual attribution cannot be confirmed.",
                Signals: new PersonAttributionSignals(CorroborationStrength.NotProvided, DistrictMatchStrength.NotProvided, CompanyLinkStrength.None, false, false, false),
                Candidates: []);
        }

        var targetNorm = PersonNameNormaliser.Normalise(target.Name);
        if (string.IsNullOrWhiteSpace(targetNorm))
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.Ambiguous,
                Rule: "invalid-target-name",
                Party: null,
                Reason: "Invalid individual target name.",
                Signals: new PersonAttributionSignals(CorroborationStrength.NotProvided, DistrictMatchStrength.NotProvided, CompanyLinkStrength.None, false, false, false),
                Candidates: []);
        }

        var targetTokens = PersonNameNormaliser.Tokenise(targetNorm);
        var isSingleCommon = PersonNameNormaliser.IsSingleCommonToken(targetNorm);

        // Parse all parties
        var parsedParties = parties.Select(PersonNameNormaliser.ParseParty).ToList();

        // Check if all parties are corporate entities
        if (parsedParties.All(p => p.IsCorporateOnly))
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.NotTheCompany,
                Rule: "corporate-entities-only",
                Party: parties[0],
                Reason: "All listed parties are corporate entities or institutions, not the individual.",
                Signals: new PersonAttributionSignals(CorroborationStrength.NotProvided, DistrictMatchStrength.NotProvided, CompanyLinkStrength.None, false, false, false),
                Candidates: []);
        }

        // Check company link among co-parties
        var companyLink = EvaluateCompanyLink(parties, target.AssociatedCompanyName);

        // Check district/state corroboration
        var districtStrength = EvaluateDistrict(target.District, target.State, caseDistrict, caseState);

        // Evaluate each party for matches against target person
        ParsedPersonParty? bestMatchParty = null;
        var relativeOnlyDetected = false;
        var massPartyDetected = false;
        var anyNameMatch = false;
        var fatherStrength = CorroborationStrength.NotProvided;

        foreach (var p in parsedParties)
        {
            var (matchesLitigant, appearsOnlyInRelative, isExact) = PersonNameNormaliser.MatchPerson(targetNorm, p);

            if (appearsOnlyInRelative)
            {
                relativeOnlyDetected = true;
            }

            if (matchesLitigant)
            {
                anyNameMatch = true;
                if (p.IsMassParty) massPartyDetected = true;

                // Evaluate father/husband corroboration for this party
                var fStrength = EvaluateFatherOrHusband(p, target.FatherOrHusbandName);
                if (fStrength > fatherStrength || bestMatchParty == null)
                {
                    fatherStrength = fStrength;
                    bestMatchParty = p;
                }
            }
        }

        var signals = new PersonAttributionSignals(
            FatherOrHusband: fatherStrength,
            District: districtStrength,
            CompanyLink: companyLink,
            MassParty: massPartyDetected,
            RelativeOnly: relativeOnlyDetected && !anyNameMatch,
            SingleCommonTokenOnly: isSingleCommon);

        // ── GATE 1: Name appears ONLY inside relative's name ──
        if (signals.RelativeOnly)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.NotTheCompany,
                Rule: "relative-inside-name",
                Party: parties.FirstOrDefault(p => p.Contains(target.Name, StringComparison.OrdinalIgnoreCase)) ?? parties[0],
                Reason: $"The individual's name appears only as a relative ({bestMatchParty?.Relationship.ToString().ToLowerInvariant() ?? "relative"}) in the record, not the litigant.",
                Signals: signals,
                Candidates: []);
        }

        // ── GATE 2: No party resembles target individual ──
        if (!anyNameMatch)
        {
            var candidates = FindCandidates(parties, targetTokens);
            if (candidates.Count > 0)
            {
                return new PersonRuleAssessment(
                    Grade: PersonAttributionGrade.Possible,
                    Outcome: LitigationAttributionOutcome.Pending,
                    Rule: "unsettled-candidates",
                    Party: null,
                    Reason: "Parties partially resemble the individual's name; awaiting judgement.",
                    Signals: signals,
                    Candidates: candidates,
                    NeedsJudgement: true);
            }

            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.NotTheCompany,
                Rule: "name-not-matched",
                Party: parties[0],
                Reason: "No listed party matches the individual's name.",
                Signals: signals,
                Candidates: []);
        }

        // ── GATE 3: Conflicting father or husband name ──
        if (fatherStrength == CorroborationStrength.Conflict)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.NotTheCompany,
                Rule: "father-husband-conflict",
                Party: bestMatchParty?.RawParty,
                Reason: $"Conflicting relative name in court record ({bestMatchParty?.RelativeName}) vs known profile ({target.FatherOrHusbandName}).",
                Signals: signals,
                Candidates: []);
        }

        // ── GATE 4: Conflicting jurisdiction with no company or father corroboration ──
        if (districtStrength == DistrictMatchStrength.Conflict && companyLink == CompanyLinkStrength.None && fatherStrength == CorroborationStrength.NotProvided)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Excluded,
                Outcome: LitigationAttributionOutcome.NotTheCompany,
                Rule: "district-conflict",
                Party: bestMatchParty?.RawParty,
                Reason: $"Conflicting district/state ({caseDistrict}, {caseState}) without corroborating company or family signals.",
                Signals: signals,
                Candidates: []);
        }

        // ── GATE 5: Mass party cause title alone cannot identify anyone ──
        if (massPartyDetected && fatherStrength == CorroborationStrength.NotProvided && companyLink == CompanyLinkStrength.None)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Possible,
                Outcome: LitigationAttributionOutcome.Ambiguous,
                Rule: "mass-party-cause-title",
                Party: bestMatchParty?.RawParty,
                Reason: "A mass-party cause title (& Ors) cannot identify the individual without corroborating father or company link.",
                Signals: signals,
                Candidates: []);
        }

        // ── GATE 6: Single common token without strong corroboration ──
        if (isSingleCommon && fatherStrength != CorroborationStrength.Full && companyLink != CompanyLinkStrength.Direct)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Possible,
                Outcome: LitigationAttributionOutcome.Ambiguous,
                Rule: "single-common-token",
                Party: bestMatchParty?.RawParty,
                Reason: $"Common single name token ({target.Name}) without strong corroboration.",
                Signals: signals,
                Candidates: []);
        }

        // ── CONFIRMED: Whole-name + Positive corroboration (Name alone NEVER reaches Confirmed) ──
        if (fatherStrength == CorroborationStrength.Full)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Confirmed,
                Outcome: LitigationAttributionOutcome.Attributed,
                Rule: "confirmed-father-match",
                Party: bestMatchParty?.RawParty,
                Reason: $"A party matches the individual's full name with matching father/husband ({bestMatchParty?.RelativeName}).",
                Signals: signals,
                Candidates: []);
        }

        if (companyLink == CompanyLinkStrength.Direct)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Confirmed,
                Outcome: LitigationAttributionOutcome.Attributed,
                Rule: "confirmed-company-coparty",
                Party: bestMatchParty?.RawParty,
                Reason: $"A party matches the individual's full name, and their associated company ({target.AssociatedCompanyName}) is a co-party in the case.",
                Signals: signals,
                Candidates: []);
        }

        // ── PROBABLE: Whole-name + Plausible secondary corroboration ──
        if (districtStrength == DistrictMatchStrength.Match)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Probable,
                Outcome: LitigationAttributionOutcome.Attributed,
                Rule: "probable-district-match",
                Party: bestMatchParty?.RawParty,
                Reason: $"A party matches the individual's full name in the same district ({caseDistrict}).",
                Signals: signals,
                Candidates: []);
        }

        if (fatherStrength == CorroborationStrength.Partial)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Probable,
                Outcome: LitigationAttributionOutcome.Attributed,
                Rule: "probable-relative-partial",
                Party: bestMatchParty?.RawParty,
                Reason: $"A party matches the individual's full name with partial relative match ({bestMatchParty?.RelativeName}).",
                Signals: signals,
                Candidates: []);
        }

        if (companyLink == CompanyLinkStrength.Lender)
        {
            return new PersonRuleAssessment(
                Grade: PersonAttributionGrade.Probable,
                Outcome: LitigationAttributionOutcome.Attributed,
                Rule: "probable-lender-link",
                Party: bestMatchParty?.RawParty,
                Reason: "A party matches the individual's full name with a financial institution as co-party in a recovery/credit dispute.",
                Signals: signals,
                Candidates: []);
        }

        // ── POSSIBLE: Name-only match without secondary signals (shown apart, unconfirmed) ──
        return new PersonRuleAssessment(
            Grade: PersonAttributionGrade.Possible,
            Outcome: LitigationAttributionOutcome.Ambiguous,
            Rule: "possible-name-only",
            Party: bestMatchParty?.RawParty,
            Reason: "A party matches the individual's name, but corroborating father/husband, address or company link is not provided.",
            Signals: signals,
            Candidates: []);
    }

    private static CorroborationStrength EvaluateFatherOrHusband(ParsedPersonParty party, string? targetRelativeName)
    {
        if (string.IsNullOrWhiteSpace(party.RelativeName) || string.IsNullOrWhiteSpace(targetRelativeName))
        {
            return CorroborationStrength.NotProvided;
        }

        var partyRelNorm = PersonNameNormaliser.Normalise(party.RelativeName);
        var targetRelNorm = PersonNameNormaliser.Normalise(targetRelativeName);

        var pTokens = PersonNameNormaliser.Tokenise(partyRelNorm);
        var tTokens = PersonNameNormaliser.Tokenise(targetRelNorm);

        if (PersonNameNormaliser.TokensEqual(pTokens, tTokens)) return CorroborationStrength.Full;
        if (PersonNameNormaliser.TokensMatch(tTokens, pTokens)) return CorroborationStrength.Partial;

        // Different relative name!
        return CorroborationStrength.Conflict;
    }

    private static DistrictMatchStrength EvaluateDistrict(string? targetDistrict, string? targetState, string? caseDistrict, string? caseState)
    {
        var hasTargetDistrict = !string.IsNullOrWhiteSpace(targetDistrict);
        var hasCaseDistrict = !string.IsNullOrWhiteSpace(caseDistrict);
        var hasTargetState = !string.IsNullOrWhiteSpace(targetState);
        var hasCaseState = !string.IsNullOrWhiteSpace(caseState);

        if (!hasTargetDistrict && !hasTargetState) return DistrictMatchStrength.NotProvided;
        if (!hasCaseDistrict && !hasCaseState) return DistrictMatchStrength.NotProvided;

        if (hasTargetDistrict && hasCaseDistrict &&
            string.Equals(targetDistrict!.Trim(), caseDistrict!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return DistrictMatchStrength.Match;
        }

        if (hasTargetState && hasCaseState &&
            string.Equals(targetState!.Trim(), caseState!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return DistrictMatchStrength.SameState;
        }

        if (hasTargetState && hasCaseState &&
            !string.Equals(targetState!.Trim(), caseState!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return DistrictMatchStrength.Conflict;
        }

        return DistrictMatchStrength.NotProvided;
    }

    private static CompanyLinkStrength EvaluateCompanyLink(IReadOnlyList<string> parties, string? associatedCompanyName)
    {
        if (string.IsNullOrWhiteSpace(associatedCompanyName)) return CompanyLinkStrength.None;

        var compNorm = associatedCompanyName.Trim().ToUpperInvariant();
        foreach (var p in parties)
        {
            var pUpper = p.ToUpperInvariant();
            if (pUpper.Contains(compNorm) || compNorm.Contains(pUpper))
            {
                return CompanyLinkStrength.Direct;
            }
        }

        // Check if any party is a recognized institutional lender
        if (parties.Any(IsFinancialInstitution))
        {
            return CompanyLinkStrength.Lender;
        }

        return CompanyLinkStrength.None;
    }

    private static bool IsFinancialInstitution(string party)
    {
        var upper = party.ToUpperInvariant();
        return upper.Contains("BANK") || upper.Contains("FINANCE") || upper.Contains("NBFC") ||
               upper.Contains("HOUSING FINANCE") || upper.Contains("ASSET RECONSTRUCTION") || upper.Contains("STATE BANK");
    }

    private static IReadOnlyList<string> FindCandidates(IReadOnlyList<string> parties, IReadOnlyList<string> targetTokens)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parties)
        {
            var parsed = PersonNameNormaliser.ParseParty(p);
            if (parsed.IsCorporateOnly) continue;
            if (parsed.Tokens.Any(t => targetTokens.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                set.Add(p);
            }
        }
        return set.Take(MaxCandidatesPerCase).ToList();
    }
}

