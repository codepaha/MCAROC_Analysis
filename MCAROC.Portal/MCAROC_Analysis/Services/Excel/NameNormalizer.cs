namespace MCAROC_Analysis.Services.Excel;

/// <summary>Conservative name normalization for matching/dedup purposes only — trim, collapse whitespace,
/// uppercase. Does not strip corporate suffixes or punctuation; NameRaw is always kept for display.</summary>
public static class NameNormalizer
{
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var collapsed = string.Join(' ', raw.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return collapsed.ToUpperInvariant();
    }

    /// <summary>Strips a trailing " (DIN : 00415231)" suffix some sheets append to director names, returning
    /// the bare name and the DIN separately if present.</summary>
    public static (string Name, string? Din) SplitDin(string raw)
    {
        var trimmed = raw.Trim();
        var marker = trimmed.LastIndexOf("(DIN", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return (trimmed, null);

        var name = trimmed[..marker].Trim();
        var dinPart = trimmed[marker..];
        var digits = new string(dinPart.Where(char.IsDigit).ToArray());
        return (name, digits.Length == 0 ? null : digits);
    }
}
