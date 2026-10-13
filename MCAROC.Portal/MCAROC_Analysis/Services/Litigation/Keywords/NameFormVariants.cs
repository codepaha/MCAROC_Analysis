using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

public sealed record GeneratedKeyword(string Value, LitigationKeywordSource Source, string Reason);

/// <summary>
/// The written forms of a company name that court records use besides the registered one (#489), from the owner's examples (2026-10-13):
/// <list type="bullet">
/// <item>corporate-form words shortened or dropped: Bharat Petroleum <b>Corp</b> Ltd, Bharat Petroleum <b>Corp.</b> Ltd., Bharat Petroleum;</item>
/// <item>a joined word split or the name run together: Hero <b>Fin Corp</b>, <b>Herofincorp</b>;</item>
/// <item>an acronym already in the name spaced or dotted: <b>H D F C</b> Bank, <b>H.D.F.C</b> Bank;</item>
/// <item>the initials of the name: <b>BPCL</b>, <b>B.P.C.L.</b>, <b>HFCL</b> (only 3+ letters).</item>
/// </list>
/// Initials are not unique ("HFCL" is also another listed company), so they are search terms only: a case found through one belongs to the company only
/// once its party is attributed (#489 decision a). No acronym is expanded here: an expansion is only accepted when its initials spell the acronym.
/// </summary>
public static partial class NameFormVariants
{
    private static readonly string[] CorporateWords = ["CORPORATION", "COMPANY"];
    private static readonly Dictionary<string, string[]> Shortened = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CORPORATION"] = ["Corp", "Corp."],
        ["COMPANY"] = ["Co", "Co."],
    };
    private const int MinCoreLetters = 5;
    private const int MinInitials = 3;

    public static IReadOnlyList<GeneratedKeyword> Generate(string? legalName)
    {
        var name = Whitespace().Replace(legalName?.Trim() ?? "", " ");
        if (name.Length == 0) return [];
        var core = PhoneticVariantGenerator.WithoutLegalSuffix(name);            // "Bharat Petroleum Corporation"
        var suffix = name.Length > core.Length ? name[core.Length..].Trim() : "";  // "Limited"
        var shortSuffix = ShortSuffix(suffix);                                     // "Ltd"
        var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var results = new List<GeneratedKeyword>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { name, core };
        void Add(string value, LitigationKeywordSource source, string reason)
        {
            value = Whitespace().Replace(value, " ").Trim();
            if (value.Length > 0 && seen.Add(value)) results.Add(new GeneratedKeyword(value, source, reason));
        }

        // Private Limited written short (a generated form, so it follows the generated-keywords switch).
        if (suffix.Equals("PRIVATE LIMITED", StringComparison.OrdinalIgnoreCase))
            Add($"{core} Pvt Ltd", LitigationKeywordSource.GeneratedNameForm, "\"Private Limited\" written as \"Pvt Ltd\"");

        // Corporate-form words: shortened, with the short legal suffix; and dropped when the rest still names something.
        for (var i = 0; i < words.Length; i++)
        {
            if (!Shortened.TryGetValue(words[i], out var shorts)) continue;
            foreach (var s in shorts)
            {
                var replaced = Join(words, i, s);
                Add(replaced, LitigationKeywordSource.GeneratedNameForm, $"\"{words[i]}\" written as \"{s}\"");
                if (shortSuffix.Length > 0)
                    Add($"{replaced} {(s.EndsWith('.') ? shortSuffix + "." : shortSuffix)}", LitigationKeywordSource.GeneratedNameForm, $"\"{words[i]} {suffix}\" written as \"{s} {shortSuffix}\"");
            }
        }
        var withoutCorporate = words.Where(w => !CorporateWords.Contains(w.ToUpperInvariant())).ToArray();
        if (withoutCorporate.Length < words.Length && withoutCorporate.Length > 0 && Letters(withoutCorporate) >= MinCoreLetters)
            Add(string.Join(' ', withoutCorporate), LitigationKeywordSource.GeneratedNameForm, "without \"Corporation\" / \"Company\"");

        // Joined words ("FinCorp"): split, and the whole name run together.
        for (var i = 0; i < words.Length; i++)
        {
            var parts = CamelParts(words[i]);
            if (parts.Count < 2) continue;
            Add(Join(words, i, string.Join(' ', parts)), LitigationKeywordSource.GeneratedNameForm, $"\"{words[i]}\" written as two words");
            Add(string.Concat(words).ToLowerInvariant() is var run && run.Length >= MinCoreLetters ? Capitalise(run) : "",
                LitigationKeywordSource.GeneratedNameForm, "the name written as one word");
        }

        // An acronym already in the name (HDFC Bank): spaced and dotted.
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length < 2 || w.Length > 6 || !w.All(char.IsUpper) || !w.All(char.IsLetter) || !IsAcronymInMixedName(words)) continue;
            Add(Join(words, i, string.Join(' ', w.ToCharArray())), LitigationKeywordSource.GeneratedNameForm, $"\"{w}\" written letter by letter");
            Add(Join(words, i, string.Join('.', w.ToCharArray())), LitigationKeywordSource.GeneratedNameForm, $"\"{w}\" written with full stops");
        }

        // Initials of the name, legal form included (Bharat Petroleum Corporation Limited = BPCL; Hero FinCorp Limited = HFCL).
        var initials = Initials(words.SelectMany(CamelParts).Concat(suffix.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s.Equals("LIMITED", StringComparison.OrdinalIgnoreCase) || s.Equals("LTD", StringComparison.OrdinalIgnoreCase))));
        var hasAcronymWord = words.Any(w => w.Length > 1 && w.All(char.IsUpper) && w.All(char.IsLetter));
        if (!hasAcronymWord && initials.Length >= MinInitials)
        {
            Add(initials, LitigationKeywordSource.GeneratedAcronym, "initials of the name");
            Add(string.Join('.', initials.ToCharArray()) + ".", LitigationKeywordSource.GeneratedAcronym, "initials of the name, with full stops");
        }
        return results;
    }

    /// <summary>True when <paramref name="expansion"/>'s initials spell <paramref name="acronym"/>, ignoring "and", "of", "the" and the legal form: the only
    /// mechanical proof that an acronym stands for those words.</summary>
    public static bool ExpandsTo(string acronym, string expansion)
    {
        var a = new string(acronym.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        var skip = new[] { "AND", "OF", "THE", "LIMITED", "LTD", "PRIVATE", "PVT", "&" };
        var letters = string.Concat(expansion.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !skip.Contains(w.ToUpperInvariant().TrimEnd('.'))).Select(w => char.ToUpperInvariant(w[0])));
        return a.Length >= 2 && a == letters;
    }

    private static bool IsAcronymInMixedName(string[] words) => words.Any(w => w.Any(char.IsLower)) || words.Length > 1;

    private static string Initials(IEnumerable<string> words)
    {
        var sb = new StringBuilder();
        foreach (var w in words)
        {
            var upper = w.ToUpperInvariant();
            if (upper is "AND" or "OF" or "THE" or "&") continue;
            if (w.Length > 1 && w.All(char.IsUpper) && w.All(char.IsLetter)) { sb.Append(upper); continue; }   // an acronym word keeps its letters
            var first = w.FirstOrDefault(char.IsLetter);
            if (first != default) sb.Append(char.ToUpperInvariant(first));
        }
        return sb.ToString();
    }

    /// <summary>"FinCorp" is Fin + Corp; "HDFC" and "Hero" are one part each.</summary>
    internal static List<string> CamelParts(string word)
    {
        if (word.Length < 4 || !word.Any(char.IsLower)) return [word];
        var parts = CamelSplit().Split(word).Where(p => p.Length > 0).ToList();
        return parts.Count >= 2 && parts.All(p => p.Length >= 2) ? parts : [word];
    }

    private static string ShortSuffix(string suffix) => suffix.ToUpperInvariant() switch
    {
        "LIMITED" => "Ltd",
        "PRIVATE LIMITED" => "Pvt Ltd",
        _ => ""
    };

    private static string Join(string[] words, int index, string replacement)
    {
        var copy = (string[])words.Clone();
        copy[index] = replacement;
        return string.Join(' ', copy);
    }

    private static int Letters(IEnumerable<string> words) => words.Sum(w => w.Count(char.IsLetter));

    private static string Capitalise(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelSplit();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
