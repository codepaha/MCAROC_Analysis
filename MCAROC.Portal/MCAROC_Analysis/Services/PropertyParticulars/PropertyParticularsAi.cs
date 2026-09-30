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
    IReadOnlyList<string> Localities, string? Village, string? Taluka, string? District, string? City, string? State, string? Pin);

public sealed record PropertyParticularsAiResult(IReadOnlyList<AiProperty> Properties);

public sealed record PropertyParticularsValidation(bool IsAccepted, PropertyParticularsAiResult? Result, IReadOnlyList<string> RejectedFields, string? FailureReason);

/// <summary>Prompt, response validation and hashing for the Gemini property-particulars extraction. The model's only
/// jobs are to split a run-on paragraph into separate properties and to label which words are which (unit, CTS
/// number, locality…); it may not add, convert or correct anything. <see cref="Validate"/> enforces that
/// mechanically: each returned value must occur in the source text, otherwise that field is dropped and recorded —
/// so a hallucinated CTS number or area can never reach the screen or a matcher.</summary>
public static partial class PropertyParticularsAi
{
    public const string PromptVersion = "1.0";
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
        - owner: only when the text says the asset belongs to another named entity (e.g. "current assets of X Private Limited").
        - Parking spaces: the total count of parking spaces for that property, as written.
        - surveyNumbers: CTS / Survey / Plot / Gat / Khasra numbers exactly as written; expand "52/1 to 17" into each number.
        Allowed values — assetClass: Immovable|Movable. kind: {{string.Join("|", Enum.GetNames<PropertyKind>())}}.
        area unit: {{string.Join("|", Enum.GetNames<AiAreaUnit>())}}. basis: {{string.Join("|", Enum.GetNames<AreaBasis>())}}.
        scheme: CTS|Survey|Plot|Gat|Khasra. qualifier: New|Old|null.
        Respond only with JSON of this shape:
        {"properties":[{"assetClass":"Immovable","kind":"Premises","owner":null,"unitNumber":null,"floor":null,"building":null,"project":null,
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

        var source = new Source(sourceText);
        var rejected = new List<string>();
        var properties = new List<AiProperty>();
        for (var i = 0; i < response.Properties.Count; i++)
        {
            var p = response.Properties[i];
            var at = $"properties[{i}]";
            if (!TryName<PropertyAssetClass>(p.AssetClass, out var assetClass) || !TryName<PropertyKind>(p.Kind, out var kind))
            {
                rejected.Add($"{at}: unsupported assetClass/kind '{p.AssetClass}'/'{p.Kind}' — property dropped");
                continue;
            }

            string? Text(string? value, string field)
            {
                if (string.IsNullOrWhiteSpace(value)) return null;
                var trimmed = Whitespace().Replace(value.Trim(), " ");
                if (source.ContainsText(trimmed)) return trimmed;
                rejected.Add($"{at}.{field}: '{trimmed}' not in source");
                return null;
            }

            var areas = new List<AiArea>();
            foreach (var a in p.Areas ?? [])
            {
                if (!TryName<AiAreaUnit>(a.Unit, out var unit) || !source.ContainsNumber(a.Value) || a.Value <= 0)
                {
                    rejected.Add($"{at}.areas: {a.Value} {a.Unit} not in source");
                    continue;
                }
                var basis = TryName<AreaBasis>(a.Basis, out var b) ? b : AreaBasis.Unspecified;
                AiAreaUnit? eqUnit = null;
                decimal? eqValue = null;
                if (a.EquivalentValue is { } ev && TryName<AiAreaUnit>(a.EquivalentUnit, out var eu))
                {
                    if (source.ContainsNumber(ev) && ev > 0) { eqValue = ev; eqUnit = eu; }
                    else rejected.Add($"{at}.areas: equivalent {ev} {a.EquivalentUnit} not in source");
                }
                areas.Add(new AiArea(a.Value, unit, basis, eqValue, eqUnit));
            }

            int? parking = null;
            if (p.ParkingSpaces is { } ps)
            {
                if (ps > 0 && source.ContainsNumber(ps)) parking = ps;
                else rejected.Add($"{at}.parkingSpaces: {ps} not in source");
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
                    var number = Whitespace().Replace(n.Trim(), "").ToUpperInvariant();
                    if (number.Length > 0 && source.ContainsSurveyNumber(number)) numbers.Add(number);
                    else rejected.Add($"{at}.surveyNumbers: {scheme} '{n}' not in source");
                }
                if (numbers.Count > 0) surveys.Add(new SurveyNumberGroup(scheme, qualifier, numbers.Distinct().ToList()));
            }

            var pin = p.Pin is { } pinText && PinFormat().IsMatch(pinText.Trim()) && source.ContainsNumber(decimal.Parse(pinText.Trim(), CultureInfo.InvariantCulture))
                ? pinText.Trim() : null;
            if (p.Pin is not null && pin is null) rejected.Add($"{at}.pin: '{p.Pin}' not in source");

            var unitNumber = p.UnitNumber is { } un && !string.IsNullOrWhiteSpace(un)
                ? (source.ContainsCompact(un) ? Whitespace().Replace(un.Trim(), "").ToUpperInvariant() : Reject($"{at}.unitNumber: '{un}' not in source"))
                : null;

            properties.Add(new AiProperty(assetClass, kind, Text(p.Owner, "owner"), unitNumber, Text(p.Floor, "floor"),
                Text(p.Building, "building"), Text(p.Project, "project"), areas, parking, surveys,
                (p.Localities ?? []).Select((l, j) => Text(l, $"localities[{j}]")).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                Text(p.Village, "village"), Text(p.Taluka, "taluka"), Text(p.District, "district"), Text(p.City, "city"), Text(p.State, "state"), pin));
        }

        if (properties.Count == 0) return new PropertyParticularsValidation(false, null, rejected, "No property survived validation.");
        return new PropertyParticularsValidation(true, new PropertyParticularsAiResult(properties), rejected, null);

        string? Reject(string reason) { rejected.Add(reason); return null; }
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

    /// <summary>The source text in the forms the grounding checks need.</summary>
    private sealed class Source
    {
        private readonly string _normalized;
        private readonly string _compact;
        private readonly HashSet<string> _digitRuns;
        private readonly HashSet<decimal> _numbers;

        public Source(string text)
        {
            _normalized = Whitespace().Replace(text, " ").ToLowerInvariant();
            _compact = NonAlphanumeric().Replace(_normalized, "");
            _digitRuns = DigitRun().Matches(text).Select(m => m.Value.TrimStart('0') is { Length: > 0 } d ? d : "0").ToHashSet();
            _numbers = Number().Matches(text)
                .Select(m => decimal.TryParse(m.Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null)
                .OfType<decimal>().ToHashSet();
        }

        public bool ContainsText(string value) =>
            _normalized.Contains(Whitespace().Replace(value, " ").ToLowerInvariant(), StringComparison.Ordinal)
            || (NonAlphanumeric().Replace(value.ToLowerInvariant(), "") is { Length: >= 3 } c && _compact.Contains(c, StringComparison.Ordinal));

        public bool ContainsCompact(string value) =>
            NonAlphanumeric().Replace(value.ToLowerInvariant(), "") is { Length: > 0 } c && _compact.Contains(c, StringComparison.Ordinal);

        public bool ContainsNumber(decimal value) => _numbers.Contains(value);

        /// <summary>"52/17" is grounded when its digit groups (52, 17) are each written in the source — which also
        /// accepts an expanded range ("52/1 to 17" → 52/17) without accepting an invented number.</summary>
        public bool ContainsSurveyNumber(string number)
        {
            var runs = DigitRun().Matches(number).Select(m => m.Value.TrimStart('0') is { Length: > 0 } d ? d : "0").ToList();
            return runs.Count > 0 && runs.All(_digitRuns.Contains);
        }
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
        string? AssetClass, string? Kind, string? Owner, string? UnitNumber, string? Floor, string? Building, string? Project,
        List<ResponseArea>? Areas, int? ParkingSpaces, List<ResponseSurvey>? SurveyNumbers, List<string>? Localities,
        string? Village, string? Taluka, string? District, string? City, string? State, string? Pin);
    private sealed record ResponseArea(decimal Value, string? Unit, string? Basis, decimal? EquivalentValue, string? EquivalentUnit);
    private sealed record ResponseSurvey(string? Scheme, string? Qualifier, List<string>? Numbers);

    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"[^a-z0-9]")] private static partial Regex NonAlphanumeric();
    [GeneratedRegex(@"\d+")] private static partial Regex DigitRun();
    [GeneratedRegex(@"\d{1,3}(?:,\d{2,3})+(?:\.\d+)?|\d+(?:\.\d+)?")] private static partial Regex Number();
    [GeneratedRegex(@"^[1-9]\d{5}$")] private static partial Regex PinFormat();
}
