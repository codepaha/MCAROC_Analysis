using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.LitigationData;

public sealed class LitigationKeywordOptions
{
    public const string SectionName = "LitigationKeywords";

    /// <summary>Whether searches use the generated keywords (name forms, initials, Hindi and phonetic spellings). Off until the attribution of the cases they
    /// find is in place (#489 PR B): a case returned through "BPCL" or a phonetic spelling must not reach any list, report or analysis unattributed.
    /// Off, the search uses the registered and former names and their legal-suffix forms only, as before, and makes no AI call.</summary>
    public bool UseGeneratedKeywords { get; set; }
}

/// <summary>
/// The keywords a company's litigation is searched with, suggested by the system with no manual step (#489). In order of priority:
/// <list type="number">
/// <item>the registered and former names and their legal-suffix forms (<see cref="LitigationKeywordPlanner"/>);</item>
/// <item>other written forms of the name: Corp for Corporation, a joined word split, an acronym spaced or dotted (<see cref="NameFormVariants"/>);</item>
/// <item>checked Devanagari spellings and written-out acronyms (<see cref="CompanyNameSuggestionService"/>);</item>
/// <item>the initials of the name (BPCL);</item>
/// <item>phonetic spellings (<see cref="PhoneticVariantGenerator"/>).</item>
/// </list>
/// At most <see cref="MaxKeywords"/>, cut from the end of that order. Every generated keyword carries its reason. The litigation data service takes them all in
/// its one search job; the portal party searches take <see cref="ForPortals"/>. A case found only through a generated keyword is the company's only once its
/// party is attributed (#489, decision a).
/// </summary>
public sealed class LitigationKeywordSuggester(CompanyNameSuggestionService suggestions, IOptions<LitigationKeywordOptions>? options = null)
{
    public const int MaxKeywords = 20;

    public async Task<IReadOnlyList<LitigationKeyword>> SuggestAsync(string legalName, IEnumerable<string>? historicalNames, long? requestId, CancellationToken ct)
    {
        var planned = LitigationKeywordPlanner.Build(legalName, historicalNames);
        if (options?.Value.UseGeneratedKeywords != true) return planned;
        var forms = NameFormVariants.Generate(legalName);
        var englishForms = planned.Select(k => k.Value).Concat(forms.Where(f => f.Source == LitigationKeywordSource.GeneratedNameForm).Select(f => f.Value)).ToList();
        var ai = await suggestions.GetAsync(legalName, englishForms, requestId, ct);
        return Combine(legalName, planned, forms, ai, PhoneticVariantGenerator.Generate(legalName));
    }

    internal static IReadOnlyList<LitigationKeyword> Combine(string legalName, IReadOnlyList<LitigationKeyword> planned, IReadOnlyList<GeneratedKeyword> forms,
        CompanyNameSuggestions ai, IEnumerable<PhoneticVariant> variants)
    {
        var result = new List<LitigationKeyword>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(LitigationKeyword k)
        {
            if (result.Count < MaxKeywords && !string.IsNullOrWhiteSpace(k.Value) && seen.Add(k.Value.Trim())) result.Add(k);
        }
        foreach (var k in planned) Add(k);
        foreach (var f in forms.Where(f => f.Source == LitigationKeywordSource.GeneratedNameForm)) Add(new(f.Value, f.Source, f.Reason));
        foreach (var s in ai.HindiSpellings) Add(new(s.Value, LitigationKeywordSource.GeneratedHindiSpelling, $"Hindi spelling, reads as \"{s.Transliteration}\""));
        var core = PhoneticVariantGenerator.WithoutLegalSuffix(legalName);
        foreach (var e in ai.Expansions)
        {
            var words = core.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var written = string.Join(' ', words.Select(w => w.Equals(e.Acronym, StringComparison.OrdinalIgnoreCase) ? e.FullForm : w));
            Add(new(written, LitigationKeywordSource.GeneratedAcronymExpansion, $"\"{e.Acronym}\" written out"));
        }
        foreach (var f in forms.Where(f => f.Source == LitigationKeywordSource.GeneratedAcronym)) Add(new(f.Value, f.Source, f.Reason));
        foreach (var v in variants) Add(new(v.Value, LitigationKeywordSource.GeneratedPhoneticVariant, v.Reason));
        return result;
    }

    /// <summary>The keywords sent to the DRT and consumer-commission party searches. Each keyword is a call per tribunal, so: Latin script only (the
    /// records probed so far are in English; Devanagari is sent only to a portal known to hold Hindi party names), no bare initials (a three-letter
    /// party search returns everything), at most <paramref name="max"/>, registered and former names first.</summary>
    public static IReadOnlyList<string> ForPortals(IEnumerable<LitigationKeyword> keywords, bool portalHoldsHindiNames = false, int max = 6) =>
        keywords
            .Where(k => k.Source != LitigationKeywordSource.GeneratedAcronym)
            .Where(k => portalHoldsHindiNames || !(k.Source is LitigationKeywordSource.GeneratedHindiSpelling or LitigationKeywordSource.ApprovedHindiAlias
                || NamePhoneticKey.IsDevanagari(k.Value)))
            .Select(k => k.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
}
