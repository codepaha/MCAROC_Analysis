using System.Globalization;

namespace MCAROC_Analysis.Services.Excel;

/// <summary>Normalizes DIN values to an 8-digit zero-padded string. The source stores DIN as a float
/// (e.g. 415231.0), which loses the leading zero — this reconstructs it. Handles double/decimal/string/int
/// inputs defensively since ExcelDataReader's cell typing can vary by workbook.</summary>
public static class DinNormalizer
{
    public static bool TryParse(object? cellValue, out string? din)
    {
        din = null;

        long? numeric = cellValue switch
        {
            null => null,
            double d => (long)d,
            decimal dec => (long)dec,
            int i => i,
            long l => l,
            string s when long.TryParse(new string(s.Where(char.IsDigit).ToArray()), out var parsed) => parsed,
            _ => null
        };

        if (numeric is null)
            return cellValue is null;

        var text = numeric.Value.ToString(CultureInfo.InvariantCulture);
        if (text.Length > 8)
            return false;

        din = text.PadLeft(8, '0');
        return true;
    }
}
