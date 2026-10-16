using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Builds the auditable keyword set submitted for one company litigation search.
///
/// The variants made here are conservative: the registered and former names and their legal-suffix forms. Other forms (initials,
/// phonetic and Hindi spellings) are added by <see cref="LitigationKeywordSuggester"/> as <b>search terms only</b>: "HFCL" does not
/// uniquely identify Hero FinCorp, so a case found through such a keyword is the company's only once its party is attributed (#489).
/// </summary>
public static partial class LitigationKeywordPlanner
{
    /// <summary>The former names not among the searched keywords — what a search started before ingestion (which
    /// supplies the former names) still has to cover. <see cref="Build"/> adds every former name verbatim
    /// (normalised), so once a search has been planned with them they are never reported again.</summary>
    public static IReadOnlyList<string> UnsearchedNames(IEnumerable<string> names, IEnumerable<LitigationKeyword> searched)
    {
        var have = searched.Select(k => CanonicalKey(NormalizeDisplay(k.Value))).ToHashSet(StringComparer.Ordinal);
        return names.Select(NormalizeDisplay).Where(n => n.Length > 0 && !have.Contains(CanonicalKey(n)))
            .DistinctBy(CanonicalKey, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<LitigationKeyword> Build(
        string legalName,
        IEnumerable<string>? historicalNames = null,
        IEnumerable<ApprovedLitigationAlias>? approvedAliases = null)
    {
        var results = new List<LitigationKeyword>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? text, LitigationKeywordSource source)
        {
            var display = NormalizeDisplay(text);
            if (display.Length == 0 || !seen.Add(CanonicalKey(display))) return;
            results.Add(new LitigationKeyword(display, source));
        }

        Add(legalName, LitigationKeywordSource.LegalName);
        foreach (var historicalName in historicalNames ?? []) Add(historicalName, LitigationKeywordSource.HistoricalLegalName);
        foreach (var alias in approvedAliases ?? []) Add(alias.Value, alias.Source);

        // A legal suffix is routinely omitted in court data. These variants do not invent an acronym or
        // alter the commercial words, so they remain safe automatic candidates for an exact-match search.
        foreach (var variant in LegalSuffixVariants(legalName)) Add(variant, LitigationKeywordSource.GeneratedLegalSuffixVariant);
        return results;
    }

    private static IEnumerable<string> LegalSuffixVariants(string legalName)
    {
        var normalized = NormalizeDisplay(legalName);
        if (normalized.Length == 0) yield break;

        var withoutSuffix = LegalSuffix().Replace(normalized, "").Trim();
        if (withoutSuffix.Length == normalized.Length || withoutSuffix.Length == 0) yield break;

        yield return withoutSuffix;
        if (normalized.EndsWith("LIMITED", StringComparison.OrdinalIgnoreCase))
            yield return withoutSuffix + " Ltd";
        else if (normalized.EndsWith("LTD", StringComparison.OrdinalIgnoreCase) || normalized.EndsWith("LTD.", StringComparison.OrdinalIgnoreCase))
            yield return withoutSuffix + " Limited";
    }

    private static string NormalizeDisplay(string? value) => value is null
        ? string.Empty
        : Whitespace().Replace(value.Normalize(NormalizationForm.FormC).Trim(), " ");

    private static string CanonicalKey(string value) => value.ToUpperInvariant();

    [GeneratedRegex(@"\s+(?:PRIVATE\s+LIMITED|PVT\.?\s+LTD\.?|LIMITED|LTD\.?)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegalSuffix();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}

/// <param name="Reason">Why a generated keyword was suggested ("\"LAKSHMI\" written as \"LAXMI\"", "Hindi spelling, reads as ..."): the provenance shown with a case
/// found through it. Null for the registered and former names.</param>
public sealed record LitigationKeyword(string Value, LitigationKeywordSource Source, string? Reason = null)
{
    private static readonly System.Text.Json.JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    /// <summary>The keywords stored on a search job (<c>LitigationSearchJob.KeywordsJson</c>). The job writes
    /// <c>{"value":…,"source":"LegalName"}</c> — lower-case names, the source as text — which a default
    /// <c>Deserialize&lt;LitigationKeyword&gt;</c> does not match (names are case-sensitive), leaving every value empty and every
    /// source <c>LegalName</c>. Names are matched ignoring case and the source may be text or a number; unreadable JSON gives an
    /// empty list.</summary>
    public static IReadOnlyList<LitigationKeyword> ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<List<LitigationKeyword>>(json, ReadOptions)?
                .Where(k => !string.IsNullOrWhiteSpace(k.Value)).ToList() ?? [];
        }
        catch (System.Text.Json.JsonException) { return []; }
    }
}

public sealed record ApprovedLitigationAlias(string Value, LitigationKeywordSource Source)
{
    public static ApprovedLitigationAlias Curated(string value) => new(value, LitigationKeywordSource.ApprovedAlias);
    public static ApprovedLitigationAlias Phonetic(string value) => new(value, LitigationKeywordSource.ApprovedPhoneticAlias);
    public static ApprovedLitigationAlias Hindi(string value) => new(value, LitigationKeywordSource.ApprovedHindiAlias);
}

public enum LitigationKeywordSource
{
    LegalName,
    HistoricalLegalName,
    GeneratedLegalSuffixVariant,
    ApprovedAlias,
    ApprovedPhoneticAlias,
    ApprovedHindiAlias,
    /// <summary>A spelling from <see cref="PhoneticVariantGenerator"/> (#489).</summary>
    GeneratedPhoneticVariant,
    /// <summary>A Devanagari spelling from <see cref="CompanyNameSuggestionService"/> that passed the back-check (#489).</summary>
    GeneratedHindiSpelling,
    /// <summary>Another written form of the name from <see cref="NameFormVariants"/>: Corp for Corporation, a joined word split, an acronym spaced (#489).</summary>
    GeneratedNameForm,
    /// <summary>The initials of the name (BPCL). Not unique, so a search term only (#489).</summary>
    GeneratedAcronym,
    /// <summary>An acronym in the name written out, accepted only when its initials spell the acronym (#489).</summary>
    GeneratedAcronymExpansion
}
