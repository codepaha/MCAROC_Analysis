using System.Globalization;

namespace MCAROC_Analysis.Services.Excel;

/// <summary>Parses numeric cells (the source stores amounts as native numbers already in Rs. Crore for the
/// sheets this ingests) plus "-"/blank as null. Always returns the original cell text so callers can keep
/// it alongside the parsed value for auditability.</summary>
public static class AmountNormalizer
{
    public static bool TryParse(object? cellValue, out decimal? value, out string? raw)
    {
        value = null;
        raw = cellValue?.ToString();

        switch (cellValue)
        {
            case null:
                return true;
            case double d:
                value = (decimal)d;
                return true;
            case decimal dec:
                value = dec;
                return true;
            case int i:
                value = i;
                return true;
            case string s:
                var text = s.Trim();
                if (text.Length == 0 || text == "-")
                    return true;

                var cleaned = text.Replace(",", "").Replace("₹", "").Trim();
                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    value = parsed;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}
