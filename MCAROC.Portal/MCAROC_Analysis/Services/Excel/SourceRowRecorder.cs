using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel;

/// <summary>Turns a raw workbook (every sheet, every row) into Layer-0 <see cref="SourceRow"/> records —
/// the verbatim staging layer the typed parsers project from. A row is recorded when it has at least one
/// non-blank cell; fully-empty rows (trailing padding from the reader) are not "source rows".</summary>
public static class SourceRowRecorder
{
    private const string CellSeparator = ""; // ASCII unit separator — cannot occur in workbook text

    public static List<SourceRow> Record(
        IReadOnlyList<SheetData> workbook, string workbookRole, long requestId, long ingestionRunId,
        long sourceDocumentId, DateTime extractedAt)
    {
        var rows = new List<SourceRow>();
        for (var s = 0; s < workbook.Count; s++)
        {
            var sheet = workbook[s];
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var cells = sheet.Rows[r].Select(Stringify).ToArray();
                if (cells.All(string.IsNullOrWhiteSpace)) continue; // reader padding, not a source row

                rows.Add(new SourceRow
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    WorkbookRole = workbookRole,
                    SheetName = sheet.Name,
                    SheetIndex = s,
                    RowNumber = r + 1,
                    CellsJson = JsonSerializer.Serialize(cells),
                    RowHash = Hash(cells),
                    ExtractedAt = extractedAt
                });
            }
        }
        return rows;
    }

    private static string? Stringify(object? cell) => cell switch
    {
        null => null,
        string s => s,
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => cell.ToString()
    };

    private static string Hash(IEnumerable<string?> cells)
    {
        var joined = string.Join(CellSeparator, cells.Select(c => c?.Trim() ?? string.Empty));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)));
    }
}
