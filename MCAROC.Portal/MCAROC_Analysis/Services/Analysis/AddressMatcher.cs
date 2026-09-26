using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.Analysis;

public enum AddressMatchStrength
{
    None,

    /// <summary>Same PIN code and a shared locality name, but no shared plot/survey/door number — the same
    /// neighbourhood, not provably the same property. Never stated as a finding on its own.</summary>
    Partial,

    /// <summary>A shared plot/survey/door number corroborated by a second, independent signal (the PIN code or
    /// a locality name). Still a text match, not a legal identification — callers phrase it as "appears to".</summary>
    Strong
}

/// <summary>The evidence behind a match, kept so a reviewer can check it against the charge instrument rather
/// than having to trust a bare yes/no.</summary>
public sealed record AddressMatchResult(
    AddressMatchStrength Strength,
    string? MatchedPinCode,
    IReadOnlyList<string> MatchedPlotNumbers,
    IReadOnlyList<string> MatchedLocalities)
{
    public static readonly AddressMatchResult NoMatch = new(AddressMatchStrength.None, null, [], []);
}

/// <summary>Decides whether a free-text property description (a charge's PropertyParticulars today; order/
/// judgment text later, see issue #191) refers to the same place as a known structured address. Addresses
/// filed in different places are never byte-identical ("Arazi Number 428,429 Bhaunti" vs "ARAZI NO 428 AND
/// 429 BHAUTI" — two real renderings of one Kanpur plot), so this compares the parts of an address that
/// identify a property rather than the whole string: plot/survey/door numbers, the PIN code, and locality
/// names. City and state names are deliberately NOT evidence — every property in Kanpur shares "Kanpur", so
/// treating it as a match signal would make any same-city mortgage look like the company's own premises.
/// Precision-biased in the same way as EntityCrossReferenceRules: a match needs two independent signals.</summary>
public static partial class AddressMatcher
{
    /// <summary>Address vocabulary that describes the kind of place rather than naming it — never a locality
    /// signal on its own (a mortgage "at Plot No. 12, Main Road" and an office "at Shop 4, Main Road" share
    /// nothing meaningful).</summary>
    private static readonly HashSet<string> GenericAddressWords = new(StringComparer.Ordinal)
    {
        "PLOT", "NUMBER", "ROAD", "STREET", "LANE", "NEAR", "OPPOSITE", "BEHIND", "BESIDE", "FLOOR", "GROUND",
        "FIRST", "SECOND", "THIRD", "FOURTH", "FIFTH", "BUILDING", "HOUSE", "BLOCK", "SECTOR", "PHASE", "AREA",
        "DISTRICT", "DIST", "TALUK", "TALUKA", "TEHSIL", "MANDAL", "VILLAGE", "POST", "OFFICE", "CITY", "TOWN",
        "STATE", "INDIA", "NAGAR", "COLONY", "ESTATE", "INDUSTRIAL", "COMPLEX", "TOWER", "TOWERS", "APARTMENT",
        "APARTMENTS", "MAIN", "CROSS", "LAYOUT", "EXTENSION", "WARD", "SURVEY", "KHASRA", "KHATA", "ARAZI",
        "GATA", "LAND", "PREMISES", "UNIT", "SHOP", "SUITE", "WING", "MARG", "PATH", "CHOWK", "BAZAR", "BAZAAR",
        "GALI", "EAST", "WEST", "NORTH", "SOUTH", "UPPER", "LOWER", "PARK", "GARDEN", "GARDENS",
        "PLAZA", "CENTRE", "CENTER", "HOUSING", "SOCIETY", "LIMITED", "PRIVATE", "WITH", "FROM", "THE", "AND",
        "SITUATED", "LYING", "BEING", "BEARING", "PART", "ADJACENT"
    };

    /// <summary>States/UTs, including the legacy spellings still common in MCA-filed addresses ("Orissa" —
    /// Coastal Projects' registered address — "Pondicherry", "Uttaranchal"). Matched per token, so a
    /// multi-word state's parts are each excluded — except "NAGAR"/"ISLANDS" (from "Dadra and Nagar Haveli",
    /// "Andaman and Nicobar Islands"), which are ordinary locality words and would otherwise block
    /// "Film Nagar"-style names.</summary>
    private static readonly HashSet<string> StateNameTokens = new(StringComparer.Ordinal)
    {
        "ANDHRA", "PRADESH", "ARUNACHAL", "ASSAM", "BIHAR", "CHHATTISGARH", "CHATTISGARH", "GOA", "GUJARAT",
        "HARYANA", "HIMACHAL", "JHARKHAND", "KARNATAKA", "KERALA", "MADHYA", "MAHARASHTRA", "MANIPUR",
        "MEGHALAYA", "MIZORAM", "NAGALAND", "ODISHA", "ORISSA", "PUNJAB", "RAJASTHAN", "SIKKIM", "TAMIL", "NADU",
        "TELANGANA", "TRIPURA", "UTTAR", "UTTARAKHAND", "UTTARANCHAL", "BENGAL", "DELHI", "JAMMU", "KASHMIR",
        "LADAKH", "PUDUCHERRY", "PONDICHERRY", "CHANDIGARH", "LAKSHADWEEP", "ANDAMAN", "NICOBAR", "DADRA",
        "HAVELI", "DAMAN", "DIU", "NCT"
    };

    /// <summary>Compares one known address against a free-text property description.</summary>
    /// <param name="knownAddress">A structured address the company itself filed (registered office, business
    /// address, EPFO establishment address).</param>
    /// <param name="propertyText">The free text that may describe the same property.</param>
    /// <param name="excludedPlaceNames">City names known from structured fields (e.g. RegisteredAddressCity,
    /// EpfoEstablishment.City) — excluded from locality evidence, same as state names.</param>
    public static AddressMatchResult Match(string? knownAddress, string? propertyText, IEnumerable<string?>? excludedPlaceNames = null)
    {
        if (string.IsNullOrWhiteSpace(knownAddress) || string.IsNullOrWhiteSpace(propertyText))
            return AddressMatchResult.NoMatch;

        var address = Normalize(knownAddress);
        var text = Normalize(propertyText);

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var place in excludedPlaceNames ?? [])
            if (!string.IsNullOrWhiteSpace(place))
                foreach (var token in Tokenize(Normalize(place)))
                    excluded.Add(token);
        foreach (var token in InferredCityTokens(address))
            excluded.Add(token);

        // ── PIN code ──
        var addressPin = ExtractPinCodes(address).LastOrDefault(); // the PIN closes an Indian address
        var pinMatched = addressPin is not null && ExtractPinCodes(text).Contains(addressPin);

        // ── Plot / survey / door numbers ──
        var textTokens = Tokenize(text);
        var textPlotKeys = textTokens.Where(IsPlotToken).Select(PlotKey).ToHashSet(StringComparer.Ordinal);
        var addressPinDigits = addressPin ?? "";
        var matchedPlots = Tokenize(address)
            .Where(IsPlotToken)
            .Where(t => PlotKey(t) != addressPinDigits && PlotKey(t).Length >= 2)
            .Where(t => textPlotKeys.Contains(PlotKey(t)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // ── Locality names ──
        var textLocalityForms = LocalityForms(textTokens);
        var matchedLocalities = LocalityCandidates(Tokenize(address), excluded)
            .Where(candidate => textLocalityForms.Any(t => LocalityEquivalent(candidate, t)))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var strength =
            matchedPlots.Count > 0 && (pinMatched || matchedLocalities.Count > 0) ? AddressMatchStrength.Strong
            : pinMatched && matchedLocalities.Count > 0 ? AddressMatchStrength.Partial
            : AddressMatchStrength.None;

        return strength == AddressMatchStrength.None
            ? AddressMatchResult.NoMatch
            : new AddressMatchResult(strength, pinMatched ? addressPin : null, matchedPlots, matchedLocalities);
    }

    /// <summary>Uppercase; "&amp;" read as "and" so "428 &amp; 429" tokenizes like "428 and 429".</summary>
    private static string Normalize(string raw) => raw.ToUpperInvariant().Replace("&", " AND ");

    /// <summary>Splits on whitespace and punctuation but keeps '-' and '/' inside a token, so a compound
    /// municipal number like "8-2-293/82/F-B-1/F" survives as one identifier rather than as a scatter of
    /// "8", "2", "82" fragments that would match almost anything.</summary>
    private static List<string> Tokenize(string normalized) =>
        TokenSplitRegex().Split(normalized)
            .Select(t => t.Trim('-', '/'))
            .Where(t => t.Length > 0)
            .ToList();

    private static bool IsPlotToken(string token) => token.Any(char.IsDigit);

    /// <summary>"A-36", "A/36" and "A36" are the same plot written three ways.</summary>
    private static string PlotKey(string token) => token.Replace("-", "").Replace("/", "");

    /// <summary>Six-digit PIN codes, allowing the "751 012" spacing some filings use. The first digit of an
    /// Indian PIN is never 0.</summary>
    private static List<string> ExtractPinCodes(string normalized) =>
        PinCodeRegex().Matches(normalized).Select(m => m.Groups[1].Value + m.Groups[2].Value).ToList();

    /// <summary>The comma segment immediately before the state or PIN segment is, in the MCA/EPFO address
    /// convention, the city — so it is excluded from locality evidence even when no structured city field was
    /// supplied (a BusinessAddress has none).</summary>
    private static IEnumerable<string> InferredCityTokens(string normalizedAddress)
    {
        var segments = normalizedAddress.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            // A PIN-only segment strips to nothing, which also counts — "..., Kanpur, 209305" has no state.
            var isStateOrPin = Tokenize(PinCodeRegex().Replace(segments[i], " ")).All(StateNameTokens.Contains);
            if (!isStateOrPin) continue;

            var cityTokens = Tokenize(segments[i - 1]);
            // Only a short, purely alphabetic segment is plausibly a city — never discard a segment that
            // carries a plot number or a long locality description.
            return cityTokens.Count <= 3 && cityTokens.All(t => t.All(char.IsLetter)) ? cityTokens : [];
        }
        return [];
    }

    /// <summary>Alphabetic address tokens that could name a locality, plus adjacent-pair concatenations so
    /// "FILM NAGAR" can meet "FILMNAGAR" (both real spellings of the same Hyderabad locality).</summary>
    private static List<string> LocalityCandidates(List<string> addressTokens, HashSet<string> excluded)
    {
        bool IsPlace(string t) => t.All(char.IsLetter) && !excluded.Contains(t) && !StateNameTokens.Contains(t);
        bool IsLocality(string t) => IsPlace(t) && t.Length >= 4 && !GenericAddressWords.Contains(t);

        var candidates = addressTokens.Where(IsLocality).ToList();
        // A pair needs one half that is a locality in its own right — "PLOT"+"NO" must never become the
        // "locality" "PLOTNO" that every plot description in the country shares.
        for (var i = 0; i + 1 < addressTokens.Count; i++)
        {
            var (a, b) = (addressTokens[i], addressTokens[i + 1]);
            if (IsPlace(a) && IsPlace(b) && (IsLocality(a) || IsLocality(b)))
                candidates.Add(a + b);
        }
        return candidates;
    }

    private static List<string> LocalityForms(List<string> textTokens)
    {
        var forms = textTokens.Where(t => t.All(char.IsLetter)).ToList();
        for (var i = 0; i + 1 < textTokens.Count; i++)
            if (textTokens[i].All(char.IsLetter) && textTokens[i + 1].All(char.IsLetter))
                forms.Add(textTokens[i] + textTokens[i + 1]);
        return forms;
    }

    /// <summary>Exact, or — for names long enough that one letter can't turn one real place into another —
    /// a single-character transliteration difference ("BHAUNTI" vs "BHAUTI", both real renderings of one
    /// Kanpur village on the same company's own filings).</summary>
    private static bool LocalityEquivalent(string a, string b)
    {
        if (a == b) return true;
        if (Math.Min(a.Length, b.Length) < 6 || Math.Abs(a.Length - b.Length) > 1) return false;
        return WithinOneEdit(a, b);
    }

    private static bool WithinOneEdit(string a, string b)
    {
        if (a.Length > b.Length) (a, b) = (b, a);
        int i = 0, j = 0, edits = 0;
        while (i < a.Length && j < b.Length)
        {
            if (a[i] == b[j]) { i++; j++; continue; }
            if (++edits > 1) return false;
            if (a.Length == b.Length) i++; // substitution
            j++;                           // insertion into the shorter string
        }
        return edits + (b.Length - j) <= 1;
    }

    [GeneratedRegex(@"[\s,;:()\[\].'""]+")]
    private static partial Regex TokenSplitRegex();

    [GeneratedRegex(@"(?<![\d/-])([1-9]\d{2})\s?(\d{3})(?![\d/-])")]
    private static partial Regex PinCodeRegex();
}
