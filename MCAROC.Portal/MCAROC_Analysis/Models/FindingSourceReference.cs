using System.Text.Json;

namespace MCAROC_Analysis.Models;

/// <summary>Provenance for a derived/computed <see cref="Data.Entities.AnalysisFinding"/> — distinct from
/// workbook-row lineage (<see cref="Data.Entities.ExtractedEntityBase"/>, rendered by
/// _WorkbookProvenance.cshtml) since a finding traces to a *set* of rows, not one. Parses
/// AnalysisFinding.SourceReferenceJson, which in practice only ever carries "entityType"/"entityIds"
/// (see ChargeRules/LitigationRules) — "sheet"/"rows" are an aspirational shape from the entity's own doc
/// comment that no rule populates today, so this never fabricates a sheet/row citation for a finding.</summary>
public sealed class FindingSourceReference
{
    public string? EntityType { get; private init; }
    public IReadOnlyList<long> EntityIds { get; private init; } = [];

    /// <summary>Never fabricated: only non-null when a rule actually supplied "sheet" in SourceReferenceJson
    /// (no rule does today), so this stays reserved for a future, more precise citation.</summary>
    public string? Sheet { get; private init; }
    public IReadOnlyList<int> Rows { get; private init; } = [];

    public static FindingSourceReference? TryParse(string? sourceReferenceJson)
    {
        if (string.IsNullOrWhiteSpace(sourceReferenceJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(sourceReferenceJson);
            var root = doc.RootElement;

            // Valid JSON that isn't an object (an array, "null", a bare number/string, ...) has no
            // properties to read at all — TryGetProperty itself throws InvalidOperationException on a
            // non-object element, so this must be checked before touching any property.
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? entityType = root.TryGetProperty("entityType", out var et) && et.ValueKind == JsonValueKind.String
                ? et.GetString() : null;
            string? sheet = root.TryGetProperty("sheet", out var sh) && sh.ValueKind == JsonValueKind.String
                ? sh.GetString() : null;

            var entityIds = ExtractIds(root, "entityIds", (JsonElement e, out long v) => e.TryGetInt64(out v));
            var rows = ExtractIds(root, "rows", (JsonElement e, out int v) => e.TryGetInt32(out v));

            if (string.IsNullOrWhiteSpace(entityType) && string.IsNullOrWhiteSpace(sheet)) return null;

            return new FindingSourceReference { EntityType = entityType, EntityIds = entityIds, Sheet = sheet, Rows = rows };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private delegate bool TryGet<T>(JsonElement element, out T value);

    /// <summary>Reads a JSON array property into a list of numbers, silently dropping (never throwing on)
    /// any element that isn't an array, isn't numeric, or numerically overflows the target type — a
    /// malformed entry should never take down the whole reference.</summary>
    private static List<T> ExtractIds<T>(JsonElement root, string propertyName, TryGet<T> tryGet)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<T>();
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Number && tryGet(element, out var value))
            {
                results.Add(value);
            }
        }
        return results;
    }

    /// <summary>"Computed value" when nothing identifiable survived parsing — never a guessed citation.</summary>
    public string DisplayText()
    {
        if (!string.IsNullOrWhiteSpace(Sheet))
        {
            return Rows.Count > 0
                ? $"Computed from: {Sheet}, row{(Rows.Count > 1 ? "s" : "")} {string.Join(", ", Rows)}"
                : $"Computed from: {Sheet}";
        }

        if (!string.IsNullOrWhiteSpace(EntityType) && EntityIds.Count > 0)
        {
            return $"Computed from: {EntityIds.Count} {EntityType} record{(EntityIds.Count > 1 ? "s" : "")}";
        }

        return "Computed value";
    }
}
