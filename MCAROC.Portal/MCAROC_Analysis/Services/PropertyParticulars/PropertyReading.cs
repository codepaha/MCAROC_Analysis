using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Excel.Parsers;

namespace MCAROC_Analysis.Services.PropertyParticulars;

public enum PropertyReadingSource { Rules, Ai }

/// <summary>One property as shown to a user — whether it came from the Gemini split or the deterministic rules.</summary>
public sealed record PropertyReadingItem(
    IReadOnlyList<PropertyAssetClass> AssetClasses,
    IReadOnlyList<PropertyKind> Kinds,
    string? Owner,
    IReadOnlyList<string> UnitLines,
    IReadOnlyList<string> BuildingsOrProjects,
    IReadOnlyList<NormalizedArea> Areas,
    int? ParkingSpaces,
    IReadOnlyList<string> ParkingSpaceNumbers,
    IReadOnlyList<SurveyNumberGroup> SurveyNumbers,
    NormalizedLocation Location,
    IReadOnlyList<string> NamedEntities);

/// <summary>The normalised reading of one charge event's property particulars, for the charge drawer and the
/// dossier. Prefers a completed, validated Gemini extraction (properties split one per asset); falls back to
/// <see cref="PropertyParticularsNormalizer"/> (one merged reading) when there is none yet, when extraction is
/// disabled, or when it failed. Never mixes the two for one text.</summary>
public sealed record PropertyReading(
    PropertyReadingSource Source, IReadOnlyList<PropertyReadingItem> Items, bool DetailsOnlyInReferencedDocument, int RejectedFieldCount)
{
    public static readonly PropertyReading None = new(PropertyReadingSource.Rules, [], false, 0);

    public bool HasContent => Items.Count > 0 || DetailsOnlyInReferencedDocument;

    public static PropertyReading For(string? propertyParticulars, string? propertyType,
        IReadOnlyDictionary<string, PropertyParticularsExtraction>? extractions)
    {
        if (string.IsNullOrWhiteSpace(propertyParticulars) || propertyParticulars.Trim() == "-") return None;
        if (extractions is not null && PropertyParticularsAi.IsExtractable(propertyParticulars)
            && extractions.TryGetValue(PropertyParticularsAi.HashOf(propertyParticulars!, propertyType), out var extraction)
            && extraction.Status == PropertyParticularsExtractionStatus.Completed
            && PropertyParticularsAi.Deserialize(extraction.ExtractionJson) is { Properties.Count: > 0 } ai)
            return FromAi(ai, CountRejected(extraction.RejectedFieldsJson));
        return FromRules(PropertyParticularsNormalizer.Normalize(propertyParticulars, propertyType));
    }

    public static PropertyReading FromRules(NormalizedPropertyParticulars p)
    {
        var hasItem = p.AssetClasses.Count > 0 || p.Kinds.Count > 0 || p.Owner is not null || p.Units.Count > 0 || p.Areas.Count > 0 || p.ParkingSpaces is not null
            || p.SurveyNumbers.Count > 0 || !p.Location.IsEmpty || p.NamedEntities.Count > 0;
        IReadOnlyList<PropertyReadingItem> items = hasItem
            ?
            [
                new PropertyReadingItem(p.AssetClasses, p.Kinds, p.Owner, p.Units.Select(PropertyParticularsNormalizer.Describe).ToList(),
                    p.BuildingsOrProjects.Where(b => p.Units.All(u => !string.Equals(u.Building, b, StringComparison.OrdinalIgnoreCase))).ToList(),
                    p.Areas, p.ParkingSpaces, p.ParkingSpaceNumbers, p.SurveyNumbers, p.Location, p.NamedEntities)
            ]
            : [];
        return new PropertyReading(PropertyReadingSource.Rules, items, p.DetailsOnlyInReferencedDocument, 0);
    }

    public static PropertyReading FromAi(PropertyParticularsAiResult result, int rejectedFieldCount) =>
        new(PropertyReadingSource.Ai, result.Properties.Select(ToItem).ToList(), false, rejectedFieldCount);

    private static PropertyReadingItem ToItem(AiProperty p)
    {
        var unitLines = new List<string>();
        if (p.UnitNumber is not null)
            unitLines.Add(PropertyParticularsNormalizer.Describe(new NormalizedUnit(p.UnitNumber, p.Floor, p.Building)));
        var buildings = new[] { p.UnitNumber is null ? p.Building : null, p.Project }.OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var location = new NormalizedLocation(p.Localities ?? [], p.Village, p.Taluka, p.District, p.City, p.State, p.Pin);
        return new PropertyReadingItem([p.AssetClass], [p.Kind], p.Owner, unitLines, buildings,
            (p.Areas ?? []).Select(ToArea).ToList(), p.ParkingSpaces, [], p.SurveyNumbers ?? [], location, []);
    }

    private const decimal SqFtPerSqM = 10.7639104167097m;

    private static decimal ToSqM(decimal value, AiAreaUnit unit) => unit switch
    {
        AiAreaUnit.SqM => value,
        AiAreaUnit.SqFt => value / SqFtPerSqM,
        AiAreaUnit.Acre => value * 4046.8564224m,
        AiAreaUnit.Hectare => value * 10000m,
        AiAreaUnit.SqYd => value * 0.83612736m,
        _ => value
    };

    /// <summary>Stated values win over converted ones: a restatement in sq m or sq ft is used as written.</summary>
    private static NormalizedArea ToArea(AiArea a)
    {
        decimal? Stated(AiAreaUnit unit) => a.Unit == unit ? a.Value : a.EquivalentUnit == unit ? a.EquivalentValue : null;
        var sqm = Stated(AiAreaUnit.SqM) ?? ToSqM(a.Value, a.Unit);
        var sqft = Stated(AiAreaUnit.SqFt) ?? sqm * SqFtPerSqM;
        return new NormalizedArea(decimal.Round(sqm, 2), decimal.Round(sqft, 2), a.Basis);
    }

    private static int CountRejected(string? rejectedFieldsJson)
    {
        if (string.IsNullOrWhiteSpace(rejectedFieldsJson)) return 0;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(rejectedFieldsJson)?.Count ?? 0; }
        catch (System.Text.Json.JsonException) { return 0; }
    }

    /// <summary>One line per reading for compact places (the dossier annexure).</summary>
    public string Summarize() =>
        string.Join("  |  ", Items.Select(Summarize).Append(DetailsOnlyInReferencedDocument ? "details only in the referenced schedule/agreement" : null).OfType<string>());

    public static string Summarize(PropertyReadingItem i)
    {
        var parts = new List<string>();
        if (i.AssetClasses.Count > 0) parts.Add(string.Join(" + ", i.AssetClasses));
        if (i.Kinds.Count > 0) parts.Add(string.Join(", ", i.Kinds.Select(PropertyParticularsNormalizer.Label)));
        if (i.Owner is not null) parts.Add($"of {i.Owner}");
        parts.AddRange(i.UnitLines);
        parts.AddRange(i.BuildingsOrProjects);
        parts.AddRange(i.Areas.Select(PropertyParticularsNormalizer.Describe));
        if (i.ParkingSpaces is { } n) parts.Add($"{n} parking space{(n == 1 ? "" : "s")}");
        parts.AddRange(i.SurveyNumbers.Select(PropertyParticularsNormalizer.Describe));
        if (!i.Location.IsEmpty) parts.Add(PropertyParticularsNormalizer.Describe(i.Location));
        if (i.NamedEntities.Count > 0) parts.Add("entities named: " + string.Join(", ", i.NamedEntities));
        return string.Join(" · ", parts);
    }
}
