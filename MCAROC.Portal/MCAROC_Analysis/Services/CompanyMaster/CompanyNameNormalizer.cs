using System.Globalization;
using System.Text;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.CompanyMaster;

/// <summary>The three derived name columns stored on <see cref="CompanyMasterRecord"/> (issue #293).</summary>
/// <param name="NameNormalized">Case/Unicode-folded, punctuation-free, with the common abbreviations expanded
/// ("ABC Pvt. Ltd." and "ABC PRIVATE LIMITED" both become "ABC PRIVATE LIMITED").</param>
/// <param name="NameCore"><paramref name="NameNormalized"/> without its legal-form suffix and a leading
/// "THE" — the part of the name that actually identifies the business ("ABC").</param>
/// <param name="EntityForm">The legal form the name's suffix declares, parsed from the name alone.</param>
public sealed record NormalizedCompanyName(string NameNormalized, string NameCore, EntityForm EntityForm);

/// <summary>Pure name normalization for identity resolution (pipeline-automation plan §5A.2 step 1). Used to
/// fill the indexed <c>NameNormalized</c>/<c>NameCore</c>/<c>EntityForm</c> columns — by the bulk import tool,
/// the MCA sync promotion and the one-off backfill — and, later, to normalize the name a requester types, so
/// both sides of a lookup go through exactly the same function.
///
/// Deliberately conservative: it folds variants of the <b>same</b> written name (case, accents, punctuation,
/// "Pvt"/"Private", "&amp;"/"and", "(P) Ltd", "M/s.") and nothing more. It does not fix typos, reorder words or
/// transliterate — those are ranking concerns for the resolver (#294), where a wrong guess costs a score, not
/// a wrong row. <see cref="Version"/> is bumped whenever the output for an existing input changes, because
/// the stored columns must then be backfilled again.</summary>
public static class CompanyNameNormalizer
{
    /// <summary>Bump when any output changes; stored columns computed by an older version need a re-backfill.</summary>
    public const int Version = 1;

    /// <summary>Column width for NameNormalized/NameCore — wider than Name's 400 because expanding
    /// abbreviations ("PVT" → "PRIVATE") lengthens a name.</summary>
    public const int MaxLength = 450;

    /// <summary>Whole-token abbreviation expansions. Only abbreviations that are unambiguous in an Indian
    /// company name are listed.</summary>
    private static readonly Dictionary<string, string[]> TokenExpansions = new(StringComparer.Ordinal)
    {
        ["PVT"] = ["PRIVATE"],
        ["PVTLTD"] = ["PRIVATE", "LIMITED"],
        ["PLTD"] = ["PRIVATE", "LIMITED"],
        ["LTD"] = ["LIMITED"],
        ["CO"] = ["COMPANY"],
        ["CORP"] = ["CORPORATION"],
        ["COOP"] = ["COOPERATIVE"],
    };

    /// <summary>Legal-form suffixes, checked in order: every multi-word suffix must precede any shorter suffix it
    /// ends with ("OPC PRIVATE LIMITED" and "PTE LIMITED" before "PRIVATE LIMITED"/"LIMITED").</summary>
    private static readonly (string[] Tokens, EntityForm Form)[] Suffixes =
    [
        (["OPC", "PRIVATE", "LIMITED"], EntityForm.OnePerson),
        (["PRIVATE", "LIMITED", "OPC"], EntityForm.OnePerson),
        (["PRODUCER", "COMPANY", "LIMITED"], EntityForm.Producer),
        (["PRODUCER", "COMPANY", "PRIVATE", "LIMITED"], EntityForm.Producer),
        (["PRIVATE", "LIMITED"], EntityForm.Private),
        (["PUBLIC", "LIMITED"], EntityForm.Public),
        (["PTE", "LIMITED"], EntityForm.Foreign),
        (["LIMITED"], EntityForm.Public),
        (["LLP"], EntityForm.Llp),
        (["INCORPORATED"], EntityForm.Foreign),
        (["INC"], EntityForm.Foreign),
        (["PLC"], EntityForm.Foreign),
        (["GMBH"], EntityForm.Foreign),
        (["LLC"], EntityForm.Foreign),
    ];

    public static NormalizedCompanyName Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new NormalizedCompanyName(string.Empty, string.Empty, EntityForm.Unknown);

        var tokens = ApplyTokenRules(Tokenize(Fold(name)));
        var normalized = Truncate(string.Join(' ', tokens));

        var (suffixLength, form) = MatchSuffix(tokens);
        var core = tokens.Take(tokens.Count - suffixLength).ToList();
        if (core.Count > 1 && core[0] == "THE") core.RemoveAt(0);
        var nameCore = core.Count > 0 ? Truncate(string.Join(' ', core)) : normalized;

        return new NormalizedCompanyName(normalized, nameCore, form);
    }

    /// <summary>Unicode compatibility-decompose, drop accents, upper-case.</summary>
    private static string Fold(string raw)
    {
        var decomposed = raw.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().ToUpperInvariant();
    }

    /// <summary>Letters and digits make tokens; "&amp;" becomes the word AND; apostrophes vanish without
    /// splitting ("SHREE'S" → "SHREES"); every other character is a separator.</summary>
    private static List<string> Tokenize(string folded)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
        }

        foreach (var c in folded)
        {
            if (char.IsLetterOrDigit(c)) current.Append(c);
            else if (c is '\'' or '’' or '`') continue;
            else if (c == '&') { Flush(); tokens.Add("AND"); }
            else Flush();
        }
        Flush();
        return tokens;
    }

    private static List<string> ApplyTokenRules(List<string> tokens)
    {
        // "M/s." / "Messrs" is a courtesy prefix, never part of the registered name.
        if (tokens.Count > 2 && tokens[0] == "M" && tokens[1] == "S") tokens.RemoveRange(0, 2);
        else if (tokens.Count > 1 && tokens[0] == "MESSRS") tokens.RemoveAt(0);

        var result = new List<string>(tokens.Count + 2);
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];

            // Dotted initialisms split into single letters by the tokenizer: "L.L.P." / "O.P.C.".
            if (Next(tokens, i, "L", "L", "P")) { result.Add("LLP"); i += 2; continue; }
            if (Next(tokens, i, "O", "P", "C")) { result.Add("OPC"); i += 2; continue; }
            // "LIMITED LIABILITY PARTNERSHIP" is the long form of LLP.
            if (Next(tokens, i, "LIMITED", "LIABILITY", "PARTNERSHIP")) { result.Add("LLP"); i += 2; continue; }
            // "CO-OPERATIVE" / "CO OP": must be handled before CO expands to COMPANY.
            if (t == "CO" && i + 1 < tokens.Count && tokens[i + 1] is "OPERATIVE" or "OP")
            {
                result.Add("COOPERATIVE"); i += 1; continue;
            }

            if (TokenExpansions.TryGetValue(t, out var expansion)) result.AddRange(expansion);
            else result.Add(t);
        }

        // "ABC (P) Ltd": a lone P directly before LIMITED is "Private".
        for (var i = 0; i + 1 < result.Count; i++)
            if (result[i] == "P" && result[i + 1] == "LIMITED")
                result[i] = "PRIVATE";

        return result;
    }

    private static bool Next(List<string> tokens, int start, params string[] expected)
    {
        if (start + expected.Length > tokens.Count) return false;
        for (var k = 0; k < expected.Length; k++)
            if (tokens[start + k] != expected[k]) return false;
        return true;
    }

    private static (int Length, EntityForm Form) MatchSuffix(List<string> tokens)
    {
        foreach (var (suffix, form) in Suffixes)
        {
            // A name that is nothing but a suffix ("LIMITED") keeps its only word as the core.
            if (suffix.Length >= tokens.Count) continue;
            if (Next(tokens, tokens.Count - suffix.Length, suffix)) return (suffix.Length, form);
        }
        return (0, EntityForm.Unknown);
    }

    private static string Truncate(string value) => value.Length <= MaxLength ? value : value[..MaxLength].TrimEnd();
}
