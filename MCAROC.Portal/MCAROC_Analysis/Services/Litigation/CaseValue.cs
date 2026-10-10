using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>What counts as "no information" in a case field, and how an incoming value is merged onto the one a case already holds.
/// One definition, so that eligibility ("is a required field missing?"), identity checks and every writer agree.
/// <para><b>Rule (owner, 2026-10-10): a missing value never erases a value we hold.</b> If a field was filled - from the court portal,
/// by an earlier report, by an analyst - and the data lake still has it empty, a later import must leave it alone.</para></summary>
public static partial class CaseValue
{
    /// <summary>Placeholders the sources use for "nothing here", compared case-insensitively on the trimmed text. The union of the lists
    /// that were previously kept separately (identity checks, the pre-login reconciliation).</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "-", "--", "---", ".", "..", "NA", "N/A", "N.A.", "NIL", "NULL", "NONE", "DUMMY", "TEST", "NOT FOUND", "0000-00-00", "00-00-0000", "00/00/0000"
    };

    /// <summary>True for null, empty or whitespace text, a placeholder (<c>NA</c>, <c>-</c>, <c>NULL</c> ...), and an empty JSON list
    /// or object (<c>[]</c>, <c>{}</c>), and any of these wrapped as a JSON string (<c>""</c>, <c>"NA"</c>), which is how the list-valued fields (parties, advocates) are stored when there are none.</summary>
    public static bool IsMissing(string? value)
    {
        if (value is null) return true;
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"' && DecodeJsonString(trimmed) is { } decoded) trimmed = decoded.Trim();
        return trimmed.Length == 0 || Placeholders.Contains(trimmed) || EmptyJson().IsMatch(trimmed);
    }

    /// <summary>The parsers hand some fields over as raw JSON, so "no value" can arrive as <c>""</c>, <c>"NA"</c> or a quoted blank.</summary>
    private static string? DecodeJsonString(string quoted)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<string>(quoted); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The value a field should hold after an incoming observation:
    /// a real incoming value replaces the held one (the newer observation wins, as before);
    /// a missing incoming value never does - the held value stays when there is one;
    /// when both are missing the held text is kept if there is any, so a source's own placeholder is stored as received the first time
    /// (these fields are source-faithful) and is not turned into null.</summary>
    public static string? Merge(string? current, string? incoming)
    {
        if (!IsMissing(incoming)) return incoming;
        if (!IsMissing(current)) return current;
        return current ?? incoming;
    }

    [GeneratedRegex(@"^(\[\s*\]|\{\s*\})$", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyJson();
}
