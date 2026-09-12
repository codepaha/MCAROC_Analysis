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

            string? entityType = root.TryGetProperty("entityType", out var et) && et.ValueKind == JsonValueKind.String
                ? et.GetString() : null;
            string? sheet = root.TryGetProperty("sheet", out var sh) && sh.ValueKind == JsonValueKind.String
                ? sh.GetString() : null;

            var entityIds = root.TryGetProperty("entityIds", out var eids) && eids.ValueKind == JsonValueKind.Array
                ? eids.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt64()).ToList()
                : [];
            var rows = root.TryGetProperty("rows", out var rws) && rws.ValueKind == JsonValueKind.Array
                ? rws.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number)
                    .Select(e => e.GetInt32()).ToList()
                : [];

            if (string.IsNullOrWhiteSpace(entityType) && string.IsNullOrWhiteSpace(sheet)) return null;

            return new FindingSourceReference { EntityType = entityType, EntityIds = entityIds, Sheet = sheet, Rows = rows };
        }
        catch (JsonException)
        {
            return null;
        }
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
