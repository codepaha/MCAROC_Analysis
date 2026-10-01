using System.Text.Json;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>#340: whether an assistant answer is possibly partial. Decided from what retrieval supplied
/// (RetrievedSourcesJson carries a Type=SearchCoverage entry when the D/L passages were a top-K sample for a
/// list/count question), never from CitedSourcesJson — the model may omit S1 from its citations, and the
/// partial-results banner must not depend on it following that instruction.</summary>
public static class ChatSearchCoverage
{
    public static bool IsPartial(string? retrievedSourcesJson)
    {
        if (string.IsNullOrEmpty(retrievedSourcesJson))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(retrievedSourcesJson);
            return doc.RootElement.ValueKind == JsonValueKind.Array
                && doc.RootElement.EnumerateArray().Any(s =>
                    s.ValueKind == JsonValueKind.Object
                    && s.TryGetProperty("Type", out var t)
                    && t.ValueKind == JsonValueKind.String
                    && t.GetString() == nameof(SourceType.SearchCoverage));
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
