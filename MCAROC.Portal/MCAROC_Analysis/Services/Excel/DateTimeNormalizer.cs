using System.Globalization;

namespace MCAROC_Analysis.Services.Excel;

/// <summary>Parses the source's date/time text formats (e.g., "9 Sep, 2026 09:32 Hours") plus native DateTime cells.
/// Strips terminal "Hours", "Hour", "hrs", "hr" and parses invariant-culture date/time.
/// "-" and blank are treated as null, not errors.</summary>
public static class DateTimeNormalizer
{
    private static readonly string[] Formats =
    [
        "d MMM, yyyy HH:mm",
        "d MMM, yyyy HH:mm:ss",
        "d MMM yyyy HH:mm",
        "d MMM yyyy HH:mm:ss",
        "d MMM, yyyy h:mm tt",
        "d MMM yyyy h:mm tt",
        "d MMM, yyyy hh:mm tt",
        "d MMM yyyy hh:mm tt",
        "d MMM, yyyy",
        "d MMM yyyy",
        "dd-MMM-yy HH:mm",
        "dd-MMM-yy HH:mm:ss",
        "dd-MMM-yyyy HH:mm",
        "dd-MMM-yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm",
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-dd"
    ];

    private static readonly string[] TerminalHourTokens = ["Hours", "Hour", "hrs", "hr"];

    public static bool TryParse(object? cellValue, out DateTime? result)
    {
        result = null;

        switch (cellValue)
        {
            case null:
                return true;
            case DateTime dt:
                result = dt;
                return true;
            case string s:
                var text = s.Trim();
                if (text.Length == 0 || text == "-")
                    return true;

                text = StripTerminalHours(text);

                if (DateTime.TryParseExact(text, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedExact))
                {
                    result = parsedExact;
                    return true;
                }

                if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    result = parsed;
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private static string StripTerminalHours(string text)
    {
        foreach (var token in TerminalHourTokens)
        {
            if (text.EndsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return text[..^token.Length].Trim();
            }
        }
        return text;
    }
}

