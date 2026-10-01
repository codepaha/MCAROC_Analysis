using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Services.PropertyParticulars;

public enum AiAreaUnit { SqFt, SqM, Acre, Hectare, SqYd }

public sealed record AiArea(decimal Value, AiAreaUnit Unit, AreaBasis Basis, decimal? EquivalentValue, AiAreaUnit? EquivalentUnit);

/// <summary>One property Gemini split out of a particulars text. Every string/number here passed
/// <see cref="PropertyParticularsAi.Validate"/>'s grounding check: it occurs in the source wording.</summary>
public sealed record AiProperty(
    PropertyAssetClass AssetClass, PropertyKind Kind, string? Owner,
    string? UnitNumber, string? Floor, string? Building, string? Project,
    IReadOnlyList<AiArea> Areas, int? ParkingSpaces, IReadOnlyList<SurveyNumberGroup> SurveyNumbers,
    IReadOnlyList<string> Localities, string? Village, string? Taluka, string? District, string? City, string? State, string? Pin,
    string? SourceText = null);

public sealed record PropertyParticularsAiResult(IReadOnlyList<AiProperty> Properties);

public sealed record PropertyParticularsValidation(bool IsAccepted, PropertyParticularsAiResult? Result, IReadOnlyList<string> RejectedFields, string? FailureReason);

/// <summary>Prompt, response validation and hashing for the Gemini property-particulars extraction. The model's only
/// jobs are to split a run-on paragraph into separate properties and to label which words are which (unit, CTS
/// number, locality…); it may not add, convert or correct anything. <see cref="Validate"/> enforces that
/// mechanically: each returned value must occur in the source text, otherwise that field is dropped and recorded —
/// so a hallucinated CTS number or area can never reach the screen or a matcher.</summary>
public static partial class PropertyParticularsAi
{
    public const string PromptVersion = "2.0";
    public const string ModelId = "gemini-2.5-flash-lite";
    internal const int MaxProperties = 20;
    internal const string PromptMarker = "You are splitting and labelling the \"Particulars of Property Charged\"";

    /// <summary>Whitespace-insensitive, so the same wording re-filed with different line breaks shares one extraction.</summary>
    public static string HashOf(string propertyParticulars, string? propertyType)
    {
        var canonical = Whitespace().Replace(propertyParticulars.Trim(), " ") + "\n" + Whitespace().Replace((propertyType ?? "").Trim(), " ");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string ComputeHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static bool IsExtractable(string? propertyParticulars) =>
        !string.IsNullOrWhiteSpace(propertyParticulars) && propertyParticulars.Trim() != "-" && propertyParticulars.Trim().Length >= 15;

    public static string BuildPrompt(string propertyParticulars, string? propertyType) => $$"""
        {{PromptMarker}} of one Indian ROC charge filing (MCA Form CHG-1/CHG-9), for a BFSI credit analyst.
        Split the text into separate properties (one per distinct asset or asset group: each premises/unit, each land parcel,
        each project, a block of current assets, a share pledge, another company's assets…). Then label the words in each.
        STRICT RULES:
        - Copy every value exactly as written in the text. Never convert units, never correct spelling, never add a value that is not written.
        - If a field is not stated for a property, use null (or [] for lists). Do not guess.
        - A large parcel the property merely "forms part of" is basis "LargerLand" on that same property, not a separate property.
        - The same area restated in another unit ("27864.63 sq ft equivalent to 2588.68 sq m") is ONE area with equivalentValue/equivalentUnit.
        - sourceText: for each property, quote verbatim the clause of the text that describes it. Clauses of different properties
          must not overlap. Every unit number, floor, area, parking count and CTS/survey number of a property must be inside its own sourceText.
        - owner: only when the text says the asset belongs to another named entity (e.g. "current assets of X Private Limited").
        - Parking spaces: the total count of parking spaces for that property, as written.
        - surveyNumbers: CTS / Survey / Plot / Gat / Khasra numbers exactly as written; expand "52/1 to 17" into each number.
        Allowed values — assetClass: Immovable|Movable. kind: {{string.Join("|", Enum.GetNames<PropertyKind>())}}.
        area unit: {{string.Join("|", Enum.GetNames<AiAreaUnit>())}}. basis: {{string.Join("|", Enum.GetNames<AreaBasis>())}}.
        scheme: CTS|Survey|Plot|Gat|Khasra. qualifier: New|Old|null.
        Respond only with JSON of this shape:
        {"properties":[{"sourceText":"exact quote","assetClass":"Immovable","kind":"Premises","owner":null,"unitNumber":null,"floor":null,"building":null,"project":null,
          "areas":[{"value":0,"unit":"SqFt","basis":"Carpet","equivalentValue":null,"equivalentUnit":null}],"parkingSpaces":null,
          "surveyNumbers":[{"scheme":"CTS","qualifier":null,"numbers":["51/B"]}],"localities":[],"village":null,"taluka":null,
          "district":null,"city":null,"state":null,"pin":null}]}
        Property type column (a hint only): {{propertyType ?? "not stated"}}
        Text:
        {{propertyParticulars}}
        """;

    public static PropertyParticularsValidation Validate(string rawJson, string sourceText)
    {
        Response? response;
        try { response = JsonSerializer.Deserialize<Response>(rawJson, JsonOptions); }
        catch (JsonException ex) { return Failed($"Invalid JSON: {ex.Message}"); }
        if (response?.Properties is null) return Failed("Response has no properties array.");
        if (response.Properties.Count > MaxProperties) return Failed($"Response split the text into {response.Properties.Count} properties (max {MaxProperties}).");

        var whole = new Grounding(sourceText);
        var rejected = new List<string>();
        var properties = new List<AiProperty>();
        var claimedSpans = new List<(int Start, int End)>();
        for (var i = 0; i < response.Properties.Count; i++)
        {
            var p = response.Properties[i];
            var at = $"properties[{i}]";
            if (!TryName<PropertyAssetClass>(p.AssetClass, out var assetClass) || !TryName<PropertyKind>(p.Kind, out var kind))
            {
                rejected.Add($"{at}: unsupported assetClass/kind '{p.AssetClass}'/'{p.Kind}' — property dropped");
                continue;
            }

            // The property's own clause: identifiers, measurements and parking must be grounded inside it, so two
            // properties in one paragraph can never trade a unit number, an area/unit or a parking count.
            if (whole.FindSpan(p.SourceText) is not { } span)
            {
                rejected.Add($"{at}: sourceText is not a verbatim quote of the source — property dropped");
                continue;
            }
            if (claimedSpans.Any(c => span.Start < c.End && c.Start < span.End))
            {
                rejected.Add($"{at}: sourceText overlaps another property's clause — property dropped");
                continue;
            }
            claimedSpans.Add(span);
            var clause = whole.Slice(span);
            var own = new Grounding(clause);

            string? Words(Grounding g, string? value, string field)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var trimmed = Whitespace().Replace(value.Trim(), " ");
                if (g.ContainsWords(trimmed)) return trimmed;
                rejected.Add($"{at}.{field}: '{trimmed}' not in {(ReferenceEquals(g, own) ? "this property's clause" : "source")}");
                return null;
            }

            var areas = new List<AiArea>();
            foreach (var a in p.Areas ?? [])
            {
                if (!TryName<AiAreaUnit>(a.Unit, out var unit) || a.Value <= 0 || !own.ContainsArea(a.Value, unit))
                {
                    rejected.Add($"{at}.areas: {a.Value} {a.Unit} is not stated in this property's clause with that unit");
                    continue;
                }
                var basis = TryName<AreaBasis>(a.Basis, out var b) ? b : AreaBasis.Unspecified;
                AiAreaUnit? eqUnit = null;
                decimal? eqValue = null;
                if (a.EquivalentValue is { } ev && TryName<AiAreaUnit>(a.EquivalentUnit, out var eu))
                {
                    if (ev > 0 && eu != unit && own.ContainsArea(ev, eu)) { eqValue = ev; eqUnit = eu; }
                    else rejected.Add($"{at}.areas: equivalent {ev} {a.EquivalentUnit} is not stated in this property's clause with that unit");
                }
                areas.Add(new AiArea(a.Value, unit, basis, eqValue, eqUnit));
            }

            int? parking = null;
            if (p.ParkingSpaces is { } ps)
            {
                if (ps > 0 && own.ContainsParkingCount(ps)) parking = ps;
                else rejected.Add($"{at}.parkingSpaces: {ps} is not stated as a parking count in this property's clause");
            }

            var surveys = new List<SurveyNumberGroup>();
            foreach (var g in p.SurveyNumbers ?? [])
            {
                var scheme = g.Scheme is { } sc && Schemes.FirstOrDefault(x => string.Equals(x, sc.Trim(), StringComparison.OrdinalIgnoreCase)) is { } known ? known : null;
                var qualifier = g.Qualifier is { } q && (q.Equals("new", StringComparison.OrdinalIgnoreCase) || q.Equals("old", StringComparison.OrdinalIgnoreCase))
                    ? char.ToUpperInvariant(q[0]) + q[1..].ToLowerInvariant() : null;
                if (scheme is null) { rejected.Add($"{at}.surveyNumbers: unsupported scheme '{g.Scheme}'"); continue; }
                var numbers = new List<string>();
                foreach (var n in g.Numbers ?? [])
                {
                    var number = Grounding.NormalizeIdentifier(n);
                    if (number.Length > 0 && own.ContainsIdentifier(scheme, number)) numbers.Add(number);
                    else rejected.Add($"{at}.surveyNumbers: {scheme} '{n}' is not written in a {scheme} list (or a range it states) in this property's clause");
                }
                if (numbers.Count > 0) surveys.Add(new SurveyNumberGroup(scheme, qualifier, numbers.Distinct().ToList()));
            }

            var pin = p.Pin is { } pinText && PinFormat().IsMatch(pinText.Trim()) && whole.ContainsPin(pinText.Trim()) ? pinText.Trim() : null;
            if (p.Pin is not null && pin is null) rejected.Add($"{at}.pin: '{p.Pin}' is not stated as a PIN in source");

            string? unitNumber = null;
            if (!string.IsNullOrWhiteSpace(p.UnitNumber))
            {
                if (own.ContainsWords(p.UnitNumber)) unitNumber = Whitespace().Replace(p.UnitNumber.Trim(), "").ToUpperInvariant();
                else rejected.Add($"{at}.unitNumber: '{p.UnitNumber}' not in this property's clause");
            }

            properties.Add(new AiProperty(assetClass, kind, Words(whole, p.Owner, "owner"), unitNumber, Words(own, p.Floor, "floor"),
                Words(whole, p.Building, "building"), Words(whole, p.Project, "project"), areas, parking, surveys,
                (p.Localities ?? []).Select((l, j) => Words(whole, l, $"localities[{j}]")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Words(whole, p.Village, "village"), Words(whole, p.Taluka, "taluka"), Words(whole, p.District, "district"),
                Words(whole, p.City, "city"), Words(whole, p.State, "state"), pin,
                clause));
        }

        if (properties.Count == 0) return new PropertyParticularsValidation(false, null, rejected, "No property survived validation.");
        return new PropertyParticularsValidation(true, new PropertyParticularsAiResult(properties), rejected, null);
    }

    public static string Serialize(PropertyParticularsAiResult result) => JsonSerializer.Serialize(result, JsonOptions);

    public static PropertyParticularsAiResult? Deserialize(string? extractionJson)
    {
        if (string.IsNullOrWhiteSpace(extractionJson)) return null;
        try { return JsonSerializer.Deserialize<PropertyParticularsAiResult>(extractionJson, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static readonly string[] Schemes = ["CTS", "Survey", "Plot", "Gat", "Khasra"];

    private static PropertyParticularsValidation Failed(string reason) => new(false, null, [], reason);

    /// <summary>Names only — a numeric string like "3" must not map to whatever member sits at that position.</summary>
    private static bool TryName<T>(string? value, out T parsed) where T : struct, Enum
    {
        parsed = default;
        if (value is null) return false;
        var name = Enum.GetNames<T>().FirstOrDefault(n => string.Equals(n, value.Trim(), StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out parsed);
    }

    /// <summary>A piece of source text (the whole paragraph, or one property's quoted clause) and the grounding
    /// checks run against it. Every check is aligned to word/number boundaries — a value can never be "found" by
    /// gluing the tail of one word to the head of the next, by borrowing a number from an unrelated field, or by
    /// matching a number while ignoring the unit it was stated in.</summary>
    private sealed partial class Grounding
    {
        private readonly string _text;
        private readonly string _lower;
        private readonly List<string> _tokens;
        /// <summary>Per scheme (CTS, Survey, Plot, Gat, Khasra): the identifiers written in a list that scheme's own label
        /// introduces ("C.T.S. Nos 51(P), 52/1 to 17"), and the ranges stated inside those lists.</summary>
        private readonly Dictionary<string, (HashSet<string> Ids, List<(string Base, int From, int To)> Ranges)> _labelled = new();

        public Grounding(string text)
        {
            _text = Whitespace().Replace(text, " ");
            _lower = _text.ToLowerInvariant();
            _tokens = Token().Matches(_lower).Select(m => m.Value).ToList();
            foreach (Match label in LabelledList().Matches(_lower))
            {
                var scheme = SchemeOf(label);
                if (!_labelled.TryGetValue(scheme, out var entry))
                    _labelled[scheme] = entry = (new HashSet<string>(), new List<(string, int, int)>());
                foreach (Match item in ListItem().Matches(label.Groups["list"].Value))
                {
                    entry.Ids.Add(NormalizeIdentifier(item.Groups["id"].Value));
                    if (item.Groups["to"].Success && RangeStart().Match(item.Groups["id"].Value) is { Success: true } start
                        && int.TryParse(start.Groups["from"].Value, CultureInfo.InvariantCulture, out var from)
                        && int.TryParse(item.Groups["to"].Value, CultureInfo.InvariantCulture, out var to)
                        && to >= from && to - from <= 500)
                        entry.Ranges.Add((NormalizeIdentifier(start.Groups["base"].Value), from, to));
                }
            }
        }

        public string Slice((int Start, int End) span) => _text[span.Start..span.End];

        /// <summary>Where a verbatim quote sits in this text (whitespace-insensitive), as a range of the original.</summary>
        public (int Start, int End)? FindSpan(string? quote)
        {
            if (string.IsNullOrWhiteSpace(quote)) return null;
            var words = Whitespace().Split(quote.Trim()).Where(w => w.Length > 0).Select(Regex.Escape);
            var m = Regex.Match(_text, string.Join(@"\s+", words), RegexOptions.IgnoreCase);
            return m.Success ? (m.Index, m.Index + m.Length) : null;
        }

        /// <summary>True when the value's words are a run of whole source words — or, for values written without the
        /// source's spacing ("5C" for "5 c"), the concatenation of a run of whole source words.</summary>
        public bool ContainsWords(string value)
        {
            var words = Token().Matches(value.ToLowerInvariant()).Select(m => m.Value).ToList();
            if (words.Count == 0) return false;
            var compact = string.Concat(words);
            for (var i = 0; i < _tokens.Count; i++)
            {
                if (i + words.Count <= _tokens.Count && words.Select((w, k) => _tokens[i + k] == w).All(x => x)) return true;
                var joined = "";
                for (var j = i; j < _tokens.Count && joined.Length < compact.Length; j++)
                {
                    joined += _tokens[j];
                    if (joined == compact) return true;
                }
            }
            return false;
        }

        /// <summary>The value must be written in this text immediately followed by that unit.</summary>
        public bool ContainsArea(decimal value, AiAreaUnit unit)
        {
            var unitPattern = unit switch
            {
                AiAreaUnit.SqFt => SqFtUnit(),
                AiAreaUnit.SqM => SqMUnit(),
                AiAreaUnit.Acre => AcreUnit(),
                AiAreaUnit.Hectare => HectareUnit(),
                _ => SqYdUnit()
            };
            return Numbers().Any(n => n.Value == value && unitPattern.IsMatch(_lower[n.End..]));
        }

        /// <summary>The count must be written in this text as a parking count ("43 car parking spaces"), never
        /// borrowed from a space number ("B-43"), an area or a survey number.</summary>
        public bool ContainsParkingCount(int count) =>
            Numbers().Any(n => n.Value == count && !n.Grouped && ParkingAfter().IsMatch(_lower[n.End..]));

        /// <summary>Six standalone digits — not part of a comma-grouped amount and not an area.</summary>
        public bool ContainsPin(string pin) =>
            Numbers().Any(n => n.Raw == pin && !AnyAreaUnit().IsMatch(_lower[n.End..]));

        /// <summary>The whole identifier (suffixes included) is written in a list introduced by <em>that scheme's</em>
        /// label, or falls inside a range such a list states explicitly ("CTS 52/1 to 17" → 52/1 … 52/17). A number
        /// written anywhere else — an area, a parking count, another scheme's list — never grounds an identifier.</summary>
        public bool ContainsIdentifier(string scheme, string number)
        {
            if (!_labelled.TryGetValue(scheme, out var entry)) return false;
            if (entry.Ids.Contains(number)) return true;
            var cut = number.LastIndexOf('/');
            return cut > 0 && int.TryParse(number[(cut + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && entry.Ranges.Any(r => r.Base == number[..cut] && n >= r.From && n <= r.To);
        }

        private static string SchemeOf(Match label) =>
            label.Groups["cts"].Success ? "CTS" : label.Groups["survey"].Success ? "Survey" : label.Groups["plot"].Success ? "Plot"
            : label.Groups["gat"].Success ? "Gat" : "Khasra";

        public static string NormalizeIdentifier(string value)
        {
            var v = Whitespace().Replace(value.Trim(), "").ToUpperInvariant();
            v = PartSuffix().Replace(v, "(P)");
            return v;
        }

        private IEnumerable<(decimal Value, string Raw, int End, bool Grouped)> Numbers() =>
            NumberToken().Matches(_lower)
                .Select(m => (Ok: decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d), d, m))
                .Where(x => x.Ok)
                .Select(x => (x.d, x.m.Value, x.m.Index + x.m.Length, x.m.Value.Contains(',')));

        [GeneratedRegex(@"[a-z0-9]+")] private static partial Regex Token();
        // A whole number as written: Indian/Western comma grouping kept together ("1,38,402"), never a fragment of
        // a longer number or decimal, and never the part after a "/" (that belongs to a survey identifier).
        [GeneratedRegex(@"(?<![\d.,/])(?:\d{1,3}(?:,\d{2,3})+|\d+)(?:\.\d+)?(?!\d)")]
        private static partial Regex NumberToken();
        // A scheme label, its "No./Nos." and the identifier list after it. Real spellings: "C.T.S", "C.T. S", "CTS-Nos",
        // "CST" (typo), "Nos51"; "Survey No."/"S. No."/"Sy. No." (but never the "S." that ends "C.T.S."); "Plot No.".
        // List items join on ","/"and"/"&"; a structured identifier ("52/7", "51 (P)") may also follow a space or ".",
        // as in "51 (P) 52(P)" and "52/6. 52/7". An item followed by "parking" or an area unit is never an identifier.
        private const string Item =
            @"(?<id>\d+[a-z]?(?![\d])(?:\s*/\s*[0-9a-z]+(?![0-9a-z]))*(?:\s*\(\s*(?:p|part)\s*\)|\s+part\b)?)(?:\s*(?:to|–)\s*(?:\d+[a-z]?\s*/\s*)?(?<to>\d+)\b)?" +
            @"(?!\s*(?:car\s*)?parking|\s*(?:sq|square|acres?|hectares?|ha)\b)";
        private const string Structured = @"(?=\d+[a-z]?\s*[/(])";

        [GeneratedRegex(
            @"(?:(?<cts>(?<![a-z])(?:c\.?\s*t\.?\s*s|cst)(?![a-z]))|(?<survey>(?<![a-z.])(?:survey|sy\.|s\.)(?![a-z]))|(?<plot>(?<![a-z])plots?(?![a-z]))|(?<gat>(?<![a-z])gat(?![a-z]))|(?<khasra>(?<![a-z])khasra(?![a-z])))" +
            @"\.?\s*[-\s]*(?:nos?\.?|numbers?)?[\s.,:-]*" +
            @"(?<list>" + Item + @"(?:(?:\s*(?:,|&|\band\b)\s*|(?:\s*\.\s*|\s+)" + Structured + @")" + Item + @")*)")]
        private static partial Regex LabelledList();
        [GeneratedRegex(Item)] private static partial Regex ListItem();
        [GeneratedRegex(@"^(?<base>\d+[a-z]?)\s*/\s*(?<from>\d+)$")] private static partial Regex RangeStart();
        [GeneratedRegex(@"\(\s*PART\s*\)$|PART$|\(P\)$")] private static partial Regex PartSuffix();
        [GeneratedRegex(@"^\s*\.?\s*(?:sq(?:uare)?\.?\s*(?:feet|fts?)\b\.?|sqft\b)")] private static partial Regex SqFtUnit();
        [GeneratedRegex(@"^\s*\.?\s*(?:sq(?:uare)?\.?\s*/?\s*m(?:e?t(?:er|re)s?|trs?|ts|ets|t)?\b\.?|sqm\b)")] private static partial Regex SqMUnit();
        [GeneratedRegex(@"^\s*acres?\b")] private static partial Regex AcreUnit();
        [GeneratedRegex(@"^\s*(?:hectares?|ha)\b")] private static partial Regex HectareUnit();
        [GeneratedRegex(@"^\s*\.?\s*sq(?:uare)?\.?\s*(?:yards?|yds?)\b")] private static partial Regex SqYdUnit();
        [GeneratedRegex(@"^\s*\.?\s*(?:sq|square|acres?|hectares?|sqm|sqft)\b")] private static partial Regex AnyAreaUnit();
        [GeneratedRegex(@"^\s*(?:nos?\.?\s*(?:of\s*)?)?(?:covered\s+|open\s+|stilt\s+)?(?:car\s*)?parking\b")] private static partial Regex ParkingAfter();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private sealed record Response(List<ResponseProperty>? Properties);
    private sealed record ResponseProperty(
        string? SourceText, string? AssetClass, string? Kind, string? Owner, string? UnitNumber, string? Floor, string? Building, string? Project,
        List<ResponseArea>? Areas, int? ParkingSpaces, List<ResponseSurvey>? SurveyNumbers, List<string>? Localities,
        string? Village, string? Taluka, string? District, string? City, string? State, string? Pin);
    private sealed record ResponseArea(decimal Value, string? Unit, string? Basis, decimal? EquivalentValue, string? EquivalentUnit);
    private sealed record ResponseSurvey(string? Scheme, string? Qualifier, List<string>? Numbers);

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^[1-9]\d{5}$")] private static partial Regex PinFormat();
}
