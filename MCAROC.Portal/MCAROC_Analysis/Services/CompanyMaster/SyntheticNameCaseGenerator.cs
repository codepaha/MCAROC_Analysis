using System.Globalization;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>Builds the synthetic half of the offline harness's labelled set (issue #293, plan §5A.4): each
/// master row is rewritten the way a requester plausibly would write it, and the case's expected answer is
/// that row's own identifier. Deterministic for a given seed, so two harness runs compare like with like.
///
/// Categories are kept separate because they test different layers: <c>CaseAndPunctuation</c>,
/// <c>SuffixVariant</c> and <c>Ampersand</c> should be absorbed by normalization alone (I1); <c>WordOrder</c> and
/// <c>Typo</c> deliberately are <b>not</b> — they measure how much the resolver's ranking (I2) must add.
/// Same-name duplicates and company/LLP twins need to know what else is in the master, so the harness runner
/// builds those from the database rather than here.</summary>
public static class SyntheticNameCaseGenerator
{
    public const string CaseAndPunctuation = "CaseAndPunctuation";
    public const string SuffixVariant = "SuffixVariant";
    public const string Ampersand = "Ampersand";
    public const string WordOrder = "WordOrder";
    public const string Typo = "Typo";

    public static IReadOnlyList<NameResolutionCase> Generate(
        IEnumerable<(string Identifier, string Name)> masterRows, int seed = 293)
    {
        var random = new Random(seed);
        var cases = new List<NameResolutionCase>();

        foreach (var (identifier, name) in masterRows)
        {
            var upper = name.Trim().ToUpperInvariant();
            if (upper.Length == 0) continue;

            cases.Add(new(ToCasualCase(AbbreviateSuffix(upper, dotted: true)), identifier, CaseAndPunctuation));

            var suffixVariant = AbbreviateSuffix(upper, dotted: false);
            if (suffixVariant != upper) cases.Add(new(suffixVariant, identifier, SuffixVariant));

            if (upper.Contains(" AND ", StringComparison.Ordinal))
                cases.Add(new(upper.Replace(" AND ", " & ", StringComparison.Ordinal), identifier, Ampersand));

            // Word-order and typo variants perturb only the core; the legal suffix is kept as written
            // (normalized), so these categories measure the core-matching problem alone.
            var normalized = CompanyNameNormalizer.Normalize(upper);
            var coreWords = normalized.NameCore.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var allWords = normalized.NameNormalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var coreStart = allWords.Length > coreWords.Length && allWords[0] == "THE" && coreWords.FirstOrDefault() != "THE" ? 1 : 0;
            var suffixWords = allWords.Skip(coreStart + coreWords.Length).ToArray();
            var suffix = suffixWords.Length > 0 ? " " + string.Join(' ', suffixWords) : "";

            if (coreWords.Length >= 2 && coreWords[0] != coreWords[1])
            {
                var swapped = (string[])coreWords.Clone();
                (swapped[0], swapped[1]) = (swapped[1], swapped[0]);
                cases.Add(new(string.Join(' ', swapped) + suffix, identifier, WordOrder));
            }

            var typo = WithTypo(coreWords, random);
            if (typo is not null) cases.Add(new(typo + suffix, identifier, Typo));
        }

        return cases;
    }

    /// <summary>"PRIVATE LIMITED" → "Pvt. Ltd." / "PVT LTD", "LIMITED" → "Ltd", "LLP" → "L.L.P." — only at the
    /// end of the name, where the legal form sits.</summary>
    private static string AbbreviateSuffix(string upper, bool dotted)
    {
        (string Long, string Short)[] map = dotted
            ? [(" PRIVATE LIMITED", " PVT. LTD."), (" LIMITED", " LTD."), (" LLP", " L.L.P.")]
            : [(" PRIVATE LIMITED", " PVT LTD"), (" LIMITED", " LTD"), (" LLP", " LIMITED LIABILITY PARTNERSHIP")];
        foreach (var (longForm, shortForm) in map)
            if (upper.EndsWith(longForm, StringComparison.Ordinal))
                return upper[..^longForm.Length] + shortForm;
        return upper;
    }

    private static string ToCasualCase(string upper) =>
        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant());

    /// <summary>Drops one interior letter from the longest core word (≥ 5 letters, so the word stays
    /// recognisable) — the commonest real typo shape.</summary>
    private static string? WithTypo(string[] coreWords, Random random)
    {
        var longest = coreWords.Select((w, i) => (w, i)).Where(x => x.w.Length >= 5 && x.w.All(char.IsLetter))
            .OrderByDescending(x => x.w.Length).ThenBy(x => x.i).FirstOrDefault();
        if (longest.w is null) return null;

        var at = random.Next(1, longest.w.Length - 1);
        var words = (string[])coreWords.Clone();
        words[longest.i] = longest.w.Remove(at, 1);
        return string.Join(' ', words);
    }
}
