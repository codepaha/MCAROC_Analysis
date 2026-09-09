using System.Globalization;

namespace MCAROC_Analysis.Services.Excel;

/// <summary>Parses the source's recurring date text format ("10 Mar, 2026") plus native DateTime cells.
/// "-" and blank are treated as null, not errors.</summary>
public static class DateNormalizer
{
    private static readonly string[] Formats = ["d MMM, yyyy", "d MMM yyyy", "dd-MMM-yy", "dd/MM/yyyy", "yyyy-MM-dd"];

    public static bool TryParse(object? cellValue, out DateOnly? result)
    {
        result = null;

        switch (cellValue)
        {
            case null:
                return true;
            case DateTime dt:
                result = DateOnly.FromDateTime(dt);
                return true;
            case string s:
                var text = s.Trim();
                if (text.Length == 0 || text == "-")
                    return true;

                if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    result = DateOnly.FromDateTime(parsed);
                    return true;
                }

                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                {
                    result = DateOnly.FromDateTime(parsed);
                    return true;
                }

                return false;
            default:
                return false;
        }
    }
}
