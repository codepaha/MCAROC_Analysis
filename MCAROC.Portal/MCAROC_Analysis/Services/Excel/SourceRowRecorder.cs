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

                var cellsJson = JsonSerializer.Serialize(cells);
                rows.Add(new SourceRow
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    WorkbookRole = workbookRole,
                    SheetName = sheet.Name,
                    SheetIndex = s,
                    RowNumber = r + 1,
                    CellsJson = cellsJson,
                    RowHash = Hash(cellsJson),
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

    /// <summary>SHA-256 of the exact serialized ordered cell array — no trimming, no null/empty
    /// coalescing — so two rows collapse only when their raw cell content is truly identical.</summary>
    private static string Hash(string cellsJson) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cellsJson)));
}
