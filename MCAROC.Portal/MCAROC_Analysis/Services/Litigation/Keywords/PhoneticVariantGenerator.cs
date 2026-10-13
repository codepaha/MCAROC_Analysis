using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Spellings of a company name that Indian court records commonly use instead of the registered one (#489): Laxmi for Lakshmi, Agarwal for Aggarwal,
/// Shri for Shree, "and" for "&amp;", Hitech for Hi-Tech. Rules only, no AI and no cost.
/// <list type="bullet">
/// <item>Only <b>distinctive</b> words are varied, one change per variant, never the legal form or a generic word (Projects, Industries, India...).</item>
/// <item>A name that is only generic words gets no variants: "India Infra Projects" spelled differently identifies nobody.</item>
/// <item>No acronym is ever invented (the planner's rule stands).</item>
/// <item>At most <see cref="MaxVariants"/> per company, in a fixed order, so a search stays bounded and repeatable.</item>
/// </list>
/// The variants are search terms. A case found only through one is not the company's until it is attributed (#489, decision a).
/// </summary>
public sealed record PhoneticVariant(string Value, string Reason);

public static partial class PhoneticVariantGenerator
{
    public const int MaxVariants = 8;
    private const int MinDistinctiveLetters = 5;

    private static readonly HashSet<string> GenericWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INDIA", "INDIAN", "INDUSTRIES", "INDUSTRY", "ENTERPRISES", "ENTERPRISE", "TRADERS", "TRADING", "SERVICES", "SERVICE", "PROJECTS", "PROJECT",
        "INFRA", "INFRASTRUCTURE", "INFRASTRUCTURES", "TECH", "TECHNOLOGIES", "TECHNOLOGY", "SOLUTIONS", "SOLUTION", "INTERNATIONAL", "GROUP", "HOLDINGS",
        "HOLDING", "LABS", "LAB", "LABORATORIES", "SYSTEMS", "SYSTEM", "GLOBAL", "VENTURES", "DEVELOPERS", "DEVELOPER", "BUILDERS", "CONSTRUCTIONS",
        "CONSTRUCTION", "REALTORS", "REALTY", "ESTATES", "ESTATE", "HOUSING", "FINANCE", "FINANCIAL", "CAPITAL", "INVESTMENTS", "INVESTMENT", "EXPORTS",
        "IMPORTS", "AGRO", "FOODS", "FOOD", "PHARMA", "PHARMACEUTICALS", "CHEMICALS", "TEXTILES", "STEEL", "STEELS", "POWER", "ENERGY", "MOTORS", "AUTO",
        "ENGINEERING", "ENGINEERS", "MANUFACTURING", "PRODUCTS", "MARKETING", "LOGISTICS", "CONSULTANTS", "CONSULTANCY", "SCIENCES", "SCIENCE",
        "HEALTHCARE", "HEALTH", "HOSPITALS", "HOSPITAL", "EDUCATION", "MEDIA", "NETWORKS", "NETWORK", "SOFTWARE", "DIGITAL", "RETAIL", "HOTELS",
        "RESORTS", "PROPERTIES", "AND", "THE", "OF", "NEW", "NATIONAL", "UNITED",
        // legal forms (never varied)
        "PRIVATE", "PVT", "LIMITED", "LTD", "LLP", "COMPANY", "CO", "CORPORATION", "CORP",
    };

    /// <summary>Honorific prefixes written several ways. They are varied, but never count as the name's distinctive part.</summary>
    private static readonly string[][] HonorificSets = [["SHREE", "SHRI", "SRI"], ["SHRIMATI", "SMT"]];

    public static IReadOnlyList<PhoneticVariant> Generate(string? legalName)
    {
        var core = WithoutLegalSuffix(legalName);
        if (core.Length == 0) return [];
        var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (DistinctiveLetters(words) < MinDistinctiveLetters) return [];

        var results = new List<PhoneticVariant>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { core };
        void Add(string candidate, string reason)
        {
            candidate = Whitespace().Replace(candidate, " ").Trim();
            if (results.Count >= MaxVariants || candidate.Length == 0 || !seen.Add(candidate)) return;
            if (DistinctiveLetters(candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries)) < MinDistinctiveLetters) return;
            results.Add(new PhoneticVariant(candidate, reason));
        }

        // Whole-name forms first: they are the commonest differences in a cause title.
        if (core.Contains('&')) Add(core.Replace("&", " and "), "\"&\" written as \"and\"");
        if (AndWord().IsMatch(core)) Add(AndWord().Replace(core, " & "), "\"and\" written as \"&\"");
        foreach (var (i, word) in words.Select((w, i) => (i, w)).Where(x => x.w.Contains('-')))
        {
            Add(Replace(words, i, word.Replace('-', ' ')), $"\"{word}\" written as two words");
            Add(Replace(words, i, word.Replace("-", "")), $"\"{word}\" written as one word");
        }

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            var honorific = HonorificSets.FirstOrDefault(set => set.Contains(word.ToUpperInvariant()));
            if (honorific is not null)
            {
                foreach (var alt in honorific.Where(a => !a.Equals(word, StringComparison.OrdinalIgnoreCase)))
                    Add(Replace(words, i, MatchCase(alt, word)), $"\"{word}\" written as \"{MatchCase(alt, word)}\"");
                continue;
            }
            if (!IsDistinctive(word)) continue;
            foreach (var alt in WordAlternations(word))
                Add(Replace(words, i, alt), $"\"{word}\" written as \"{alt}\"");
        }
        return results;
    }

    /// <summary>The Indian spelling alternations of one word, each a single change, in a fixed order.</summary>
    internal static IEnumerable<string> WordAlternations(string word)
    {
        var lower = word.ToLowerInvariant();
        IEnumerable<string> Raw()
        {
            if (lower.Contains("ksh")) yield return lower.Replace("ksh", "x");
            if (Regex.IsMatch(lower, "[aeiou]x")) yield return Regex.Replace(lower, "(?<=[aeiou])x", "ksh");
            if (lower.Contains("ee")) yield return lower.Replace("ee", "i");
            if (lower.Contains("oo")) yield return lower.Replace("oo", "u");
            if (lower.Contains("aa")) yield return lower.Replace("aa", "a");
            if (lower.Length > 3 && lower.EndsWith('i') && !lower.EndsWith("ii")) yield return lower[..^1] + "ee";
            if (lower.Contains('w')) yield return lower.Replace('w', 'v');
            if (lower.Contains('v')) yield return lower.Replace('v', 'w');
            if (lower.Contains("ph")) yield return lower.Replace("ph", "f");
            else if (lower.Contains('f')) yield return lower.Replace("f", "ph");
            if (lower.Contains('z')) yield return lower.Replace('z', 'j');
            var single = DoubleConsonant().Replace(lower, "$1");
            if (single != lower) yield return single;
            // A silent final "a": Ram / Rama, Shyam / Shyama. Only short words ending in a vowel + m/n, where the habit is common.
            if (lower.Length <= 5 && Regex.IsMatch(lower, "[aeiou][mn]$")) yield return lower + "a";
        }
        return Raw().Where(v => v != lower).Distinct().Select(v => MatchCase(v, word));
    }

    private static bool IsDistinctive(string word) =>
        word.Length >= 3 && word.All(char.IsLetter) && !GenericWords.Contains(word) && !HonorificSets.Any(s => s.Contains(word.ToUpperInvariant()));

    private static int DistinctiveLetters(IEnumerable<string> words) =>
        words.Where(w => !GenericWords.Contains(w.Replace("-", "")) && !HonorificSets.Any(s => s.Contains(w.ToUpperInvariant())))
            .Sum(w => w.Count(char.IsLetter));

    private static string Replace(string[] words, int index, string replacement)
    {
        var copy = (string[])words.Clone();
        copy[index] = replacement;
        return string.Join(' ', copy);
    }

    private static string MatchCase(string value, string like)
    {
        if (like.All(c => !char.IsLetter(c) || char.IsUpper(c))) return value.ToUpperInvariant();
        if (char.IsUpper(like[0])) return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
        return value.ToLowerInvariant();
    }

    internal static string WithoutLegalSuffix(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var t = Whitespace().Replace(name.Trim(), " ");
        t = MessrsPrefix().Replace(t, "").Trim();
        string before;
        do { before = t; t = LegalSuffix().Replace(t, "").Trim(); } while (t != before && t.Length > 0);
        return t;
    }

    [GeneratedRegex(@"\s*\b(?:PRIVATE|PVT\.?|LIMITED|LTD\.?|LLP|COMPANY|CO\.?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalSuffix();

    [GeneratedRegex(@"^M\s*/\s*S\.?\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MessrsPrefix();

    [GeneratedRegex(@"\s+AND\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AndWord();

    [GeneratedRegex(@"([bcdfgjklmnpqrstvwxz])\1")]
    private static partial Regex DoubleConsonant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
