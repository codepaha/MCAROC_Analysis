using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Director - Association History" sheet — each row is one designation stint a
/// director held at this company. Header row 0: DIRECTOR NAME | DESIGNATION | APPOINTMENT DATE |
/// CESSATION DATE. A director can appear multiple times.</summary>
public static class DirectorAssociationHistoryParser
{
    public static ParseResult<DirectorAssignmentHistory> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<DirectorAssignmentHistory>();

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
            var h = new DirectorAssignmentHistory
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                DirectorNameRaw = name,
                DirectorDin = din ?? "",
                Designation = Cell(row, 1)
            };
            if (DateNormalizer.TryParse(row.Count > 2 ? row[2] : null, out var appt)) h.AppointmentDate = appt;
            if (DateNormalizer.TryParse(row.Count > 3 ? row[3] : null, out var cess)) h.CessationDate = cess;

            result.Items.Add(h);
        }

        return result;
    }

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
