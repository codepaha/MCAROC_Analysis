using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData.Attribution;

public enum PersonRelationship
{
    None = 0,
    Father = 1,
    Husband = 2,
    CareOf = 3,
    Representative = 4
}

public sealed record ParsedPersonParty(
    string RawParty,
    string PersonName,
    string? RelativeName,
    PersonRelationship Relationship,
    bool IsMassParty,
    bool IsCorporateOnly,
    IReadOnlyList<string> Tokens);

/// <summary>
/// Normalises and parses individual person names in Indian court records (#452).
/// Strips honorifics, canonicalises spelling variants/transliterations (e.g. Md/Mohd -> Mohammed),
/// extracts patronymics (S/O, D/O, W/O, C/O), detects mass-party cause titles (&amp; Ors),
/// and enforces token-boundary matching so a name is never matched by substring or single common token.
/// </summary>
public static partial class PersonNameNormaliser
{
    private static readonly HashSet<string> Honorifics = new(StringComparer.OrdinalIgnoreCase)
    {
        "SHRI", "SHREE", "SRI", "SMT", "SHRIMATI", "LATE", "MR", "MRS", "MS", "MISS",
        "DR", "DOCTOR", "ADV", "ADVOCATE", "CA", "CS", "JUSTICE", "HONBLE", "HON'BLE",
        "ER", "PROF", "PROFESSOR", "PANDIT", "PT", "SWAMI", "THIRU", "THIRUMATHI"
    };

    private static readonly HashSet<string> CorporateKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "LTD", "LIMITED", "PVT", "PRIVATE", "LLP", "BANK", "CORPORATION", "CORP", "INC",
        "GOVERNMENT", "UNION OF INDIA", "STATE OF", "AUTHORITY", "BOARD", "TRUST", "SOCIETY",
        "FEDERATION", "ASSOCIATION", "ENTERPRISES", "INDUSTRIES", "HOLDINGS"
    };

    public static readonly HashSet<string> CommonTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "KUMAR", "SINGH", "SHARMA", "VERMA", "GUPTA", "PATEL", "SHAH", "YADAV", "DAS",
        "ROY", "ALI", "KHAN", "JAIN", "CHOUDHARY", "REDDY", "RAO", "MISHRA", "TIWARI",
        "PANDEY", "PRASAD", "LAL", "CHAND", "DEVI", "BEGUM", "BIBI"
    };

    /// <summary>
    /// Parses a court record party string into its constituent person name, relative name, relationship,
    /// mass party indicator, and tokens.
    /// </summary>
    public static ParsedPersonParty ParseParty(string? rawParty)
    {
        if (string.IsNullOrWhiteSpace(rawParty))
        {
            return new ParsedPersonParty(string.Empty, string.Empty, null, PersonRelationship.None, false, false, []);
        }

        var trimmed = rawParty.Trim();
        var massParty = MassPartyRegex().IsMatch(trimmed);
        var withoutMass = MassPartyRegex().Replace(trimmed, " ").Trim();

        // Check if entire party is corporate entity
        var isCorporate = IsCorporateEntity(withoutMass);

        // Extract patronymic/relative if present
        var (personPart, relativePart, rel) = ExtractRelative(withoutMass);

        var normPerson = Normalise(personPart);
        var normRelative = relativePart != null ? Normalise(relativePart) : null;
        var tokens = Tokenise(normPerson);

        return new ParsedPersonParty(
            RawParty: trimmed,
            PersonName: normPerson,
            RelativeName: normRelative,
            Relationship: rel,
            IsMassParty: massParty,
            IsCorporateOnly: isCorporate,
            Tokens: tokens);
    }

    /// <summary>
    /// Normalises a person name: strips honorifics, expands/canonicalises transliterations,
    /// and standardises spacing.
    /// </summary>
    public static string Normalise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        // Strip leading list numbers ("1. ", "2) ", etc.)
        var t = ListNumberRegex().Replace(name.Trim(), "");

        // Replace punctuation with spaces
        t = NonAlnumRegex().Replace(t, " ");

        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(CanonicaliseWord)
            .Where(w => !Honorifics.Contains(w))
            .ToList();

        return string.Join(" ", words);
    }

    /// <summary>
    /// Canonicalises specific word variants, especially Muhammad/Mohd transliterations and abbreviations.
    /// </summary>
    public static string CanonicaliseWord(string word)
    {
        var upper = word.ToUpperInvariant();
        return upper switch
        {
            "MD" or "MOHD" or "MOHAMAD" or "MOHAMMAD" or "MUHAMMAD" or "MUHAMMED" or "MOHD." or "MD." => "MOHAMMED",
            _ => upper
        };
    }

    public static IReadOnlyList<string> Tokenise(string normalisedName) =>
        normalisedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Extracts patronymic and relative clauses: S/O, D/O, W/O, C/O.
    /// Returns (personPart, relativePart, relationship).
    /// </summary>
    public static (string PersonPart, string? RelativePart, PersonRelationship Relationship) ExtractRelative(string text)
    {
        // Check Son of
        var mSon = SonOfRegex().Match(text);
        if (mSon.Success)
        {
            var person = text[..mSon.Index].Trim();
            var relative = text[(mSon.Index + mSon.Length)..].Trim();
            return (person, relative, PersonRelationship.Father);
        }

        // Check Daughter of
        var mDaughter = DaughterOfRegex().Match(text);
        if (mDaughter.Success)
        {
            var person = text[..mDaughter.Index].Trim();
            var relative = text[(mDaughter.Index + mDaughter.Length)..].Trim();
            return (person, relative, PersonRelationship.Father);
        }

        // Check Wife of
        var mWife = WifeOfRegex().Match(text);
        if (mWife.Success)
        {
            var person = text[..mWife.Index].Trim();
            var relative = text[(mWife.Index + mWife.Length)..].Trim();
            return (person, relative, PersonRelationship.Husband);
        }

        // Check Care of
        var mCare = CareOfRegex().Match(text);
        if (mCare.Success)
        {
            var person = text[..mCare.Index].Trim();
            var relative = text[(mCare.Index + mCare.Length)..].Trim();
            return (person, relative, PersonRelationship.CareOf);
        }

        // Check Representative
        var mRep = RepByRegex().Match(text);
        if (mRep.Success)
        {
            var person = text[..mRep.Index].Trim();
            var relative = text[(mRep.Index + mRep.Length)..].Trim();
            return (person, relative, PersonRelationship.Representative);
        }

        return (text, null, PersonRelationship.None);
    }

    /// <summary>
    /// Checks whether target name is a single common token (e.g. "KUMAR", "SINGH").
    /// </summary>
    public static bool IsSingleCommonToken(string normalisedName)
    {
        var tokens = Tokenise(normalisedName);
        return tokens.Count == 1 && CommonTokens.Contains(tokens[0]);
    }

    /// <summary>
    /// Matches target person against a parsed party.
    /// Never matches on substring. Requires full token alignment or compatible initial expansion.
    /// Returns:
    /// - MatchesLitigant: True if the actual litigant matches the target.
    /// - AppearsOnlyInRelative: True if the target name appears ONLY as the relative (e.g. S/O target).
    /// </summary>
    public static (bool MatchesLitigant, bool AppearsOnlyInRelative, bool IsExact) MatchPerson(
        string targetNormalised,
        ParsedPersonParty parsed)
    {
        if (string.IsNullOrWhiteSpace(targetNormalised)) return (false, false, false);
        var targetTokens = Tokenise(targetNormalised);
        if (targetTokens.Count == 0) return (false, false, false);

        // Check if target matches relative
        if (!string.IsNullOrWhiteSpace(parsed.RelativeName))
        {
            var relTokens = Tokenise(parsed.RelativeName);
            if (TokensMatch(targetTokens, relTokens))
            {
                // If litigant also matches, litigant wins; otherwise it's RelativeOnly!
                var litigantMatches = TokensMatch(targetTokens, parsed.Tokens);
                return (litigantMatches, !litigantMatches, litigantMatches && TokensEqual(targetTokens, parsed.Tokens));
            }
        }

        // Check litigant
        if (parsed.Tokens.Count == 0) return (false, false, false);

        var match = TokensMatch(targetTokens, parsed.Tokens);
        var exact = match && TokensEqual(targetTokens, parsed.Tokens);
        return (match, false, exact);
    }

    /// <summary>
    /// Checks whether two sets of name tokens match.
    /// Supports exact matching and single-letter initial expansion (e.g. "R K SHARMA" vs "RAMESH KUMAR SHARMA").
    /// Substring matching is strictly rejected.
    /// </summary>
    public static bool TokensMatch(IReadOnlyList<string> target, IReadOnlyList<string> candidate)
    {
        if (target.Count == 0 || candidate.Count == 0) return false;

        // Identical
        if (TokensEqual(target, candidate)) return true;

        // If candidate has different number of tokens
        if (target.Count == candidate.Count)
        {
            // Check initial compatibility
            for (var i = 0; i < target.Count; i++)
            {
                var t = target[i];
                var c = candidate[i];
                if (t == c) continue;
                if ((t.Length == 1 && c.StartsWith(t, StringComparison.Ordinal)) ||
                    (c.Length == 1 && t.StartsWith(c, StringComparison.Ordinal)))
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        // Single token common name never matches a multi-token name
        if (target.Count == 1 || candidate.Count == 1) return false;

        return false;
    }

    public static bool TokensEqual(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsCorporateEntity(string text)
    {
        var upper = text.ToUpperInvariant();
        return CorporateKeywords.Any(k => Regex.IsMatch(upper, $@"\b{Regex.Escape(k)}\b"));
    }

    [GeneratedRegex(@"^\d{1,3}[.)]?\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListNumberRegex();

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnumRegex();

    [GeneratedRegex(@"\b(?:S/O|S\.O\.|SO|SON\s+OF|S/D/W/O)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SonOfRegex();

    [GeneratedRegex(@"\b(?:D/O|D\.O\.|DO|DAUGHTER\s+OF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DaughterOfRegex();

    [GeneratedRegex(@"\b(?:W/O|W\.O\.|WO|WIFE\s+OF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WifeOfRegex();

    [GeneratedRegex(@"\b(?:C/O|C\.O\.|CO|CARE\s+OF)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CareOfRegex();

    [GeneratedRegex(@"\b(?:REP\.?\s*BY|REPD\.?\s*BY|REPRESENTED\s+BY|THR\.?\s*ITS|THROUGH\s+ITS)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RepByRegex();

    [GeneratedRegex(@"[\s,]*(?:&|\bAND\b)\s*(?:\d{1,4}\s*)?(?:ANR|ANOTHER|ORS|OTHERS)\b[\s,.;)]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MassPartyRegex();
}
