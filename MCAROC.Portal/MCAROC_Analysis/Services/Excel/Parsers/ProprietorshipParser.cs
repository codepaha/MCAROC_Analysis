using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Proprietorship" sheet — proprietorship businesses a director is associated with.
/// Header row 0: DIRECTOR NAME | LEGAL NAME | BUSINESS NAMES | PAN | STATUS. The director name cell
/// carries an embedded "(DIN : ...)".</summary>
public static class ProprietorshipParser
{
    public static ParseResult<ProprietorshipAssociation> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<ProprietorshipAssociation>();

        var headerRow = -1;
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 4); r++)
        {
            var c0 = sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim().ToUpperInvariant() : null;
            if (c0 is "DIRECTOR NAME") { headerRow = r; break; }
        }
        if (headerRow < 0) return result;

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var raw = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(raw)) continue;

            var (name, din) = NameNormalizer.SplitDin(raw);
            result.Items.Add(new ProprietorshipAssociation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                DirectorNameRaw = name,
                DirectorDin = din ?? "",
                LegalName = Cell(row, 1),
                BusinessNames = Cell(row, 2),
                Pan = Cell(row, 3),
                Status = Cell(row, 4)
            });
        }

        return result;
    }

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
