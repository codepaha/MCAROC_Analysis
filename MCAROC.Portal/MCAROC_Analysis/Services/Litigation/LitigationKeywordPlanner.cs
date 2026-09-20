using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Builds the auditable keyword set submitted for one company litigation search.
///
/// Generated English variants are deliberately conservative. Abbreviations, phonetic spellings and
/// non-Latin spellings must be supplied as approved aliases: guessing them would turn a search term into
/// an unreviewed identity assertion (for example, "HFCL" does not uniquely identify Hero FinCorp).
/// </summary>
public static partial class LitigationKeywordPlanner
{
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

public sealed record LitigationKeyword(string Value, LitigationKeywordSource Source);

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
    ApprovedHindiAlias
}
