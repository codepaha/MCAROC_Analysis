using System.Text.Json;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Reads the party and advocate lists a litigation report carries. The provider sends each party as a record —
/// <c>{"name":"BANK OF MAHARASHTRA","address":"…","advocate":"…"}</c> — and an advocate as <c>{"name":"…","code":"…"}</c>; older and
/// hand-built data have plain strings. Both are read. A list that cannot be read gives no names, never a guess. This is the one reader
/// for every place that shows or compares parties: a reader that expected only strings silently found no parties on real reports.</summary>
public static class LitigationPartyNames
{
    public static List<string> Parse(string? json)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(json)) return names;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in doc.RootElement.EnumerateArray()) Add(names, item);
            else Add(names, doc.RootElement);
        }
        catch (JsonException) { /* unreadable: no names */ }
        return names;
    }

    private static void Add(List<string> names, JsonElement item)
    {
        var name = item.ValueKind switch
        {
            JsonValueKind.String => item.GetString(),
            JsonValueKind.Object when item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String => n.GetString(),
            _ => null
        };
        name = name?.Trim();
        // The provider sends blank names (" ") for a party it has no name for; those are not parties.
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
    }
}
