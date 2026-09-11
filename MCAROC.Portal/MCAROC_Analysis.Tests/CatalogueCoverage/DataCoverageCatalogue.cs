using System.Text.Json;
using System.Text.Json.Serialization;

namespace MCAROC_Analysis.Tests.CatalogueCoverage;

/// <summary>Loose C# mirror of <c>docs/data-coverage-catalogue.json</c> — deliberately just the fields
/// the A7 (#38) coverage checker reads, not a full schema. Loaded fresh per test so the checker always
/// reflects the file on disk.</summary>
public sealed record CatalogueRoot(
    [property: JsonPropertyName("sheets")] List<CatalogueSheet> Sheets,
    [property: JsonPropertyName("gaps")] List<CatalogueGap> Gaps)
{
    public static CatalogueRoot Load(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "data-coverage-catalogue.json");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CatalogueRoot>(json, JsonOpts)
            ?? throw new InvalidOperationException($"Could not parse {path}");
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
}

public sealed record CatalogueSheet(
    [property: JsonPropertyName("workbook")] string Workbook,
    [property: JsonPropertyName("sheet")] string Sheet,
    [property: JsonPropertyName("fields")] List<CatalogueField> Fields)
{
    /// <summary>Some catalogue rows cover more than one real sheet that shares the same column layout
    /// (e.g. "Open Charges Sequence / Satisfied Charges Sequence") — split on " / ".</summary>
    public IEnumerable<string> SheetNames => Sheet.Split(" / ", StringSplitOptions.TrimEntries);
}

public sealed record CatalogueField(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("gap")] string? Gap)
{
    /// <summary>The catalogue packs several real column names into one prose "source" string, separated
    /// by "/" — sometimes spaced ("A / B"), sometimes not ("A/B/C"). Split on the bare character so both
    /// forms produce individually matchable chunks (normalization handles any parenthetical detail within
    /// a chunk).</summary>
    public IEnumerable<string> Chunks => Source.Split('/', StringSplitOptions.TrimEntries)
        .Where(c => c.Length > 0);
}

public sealed record CatalogueGap(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string? Status);
