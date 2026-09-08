using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Directors": row 0 is the header, data from row 1.
/// Columns: NAME, DIN, PRESENT DESIGNATION, PRESENT DESIGNATION APPOINTMENT DATE,
/// ORIGINAL APPOINTMENT DATE, DATE OF CESSATION, FLAGS.</summary>
public static class DirectorsParser
{
    private const string ParserName = nameof(DirectorsParser);

    public static ParseResult<Director> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<Director>();

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (row.Count == 0 || row[0] is null) continue;

            var nameRaw = row[0]?.ToString()?.Trim() ?? string.Empty;
            if (nameRaw.Length == 0) continue;

            if (!DinNormalizer.TryParse(Cell(row, 1), out var din) || din is null)
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "DIN", Cell(row, 1)?.ToString(),
                    "BAD_DIN", $"Could not parse DIN for director '{nameRaw}'", r + 1));
                continue;
            }

            var director = new Director
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                Din = din,
                NameRaw = nameRaw,
                NameNormalized = NameNormalizer.Normalize(nameRaw),
                Designation = Cell(row, 2)?.ToString()?.Trim()
            };

            if (!DateNormalizer.TryParse(Cell(row, 3), out var desigDate))
                Warn(result, r, "DesignationAppointmentDate", Cell(row, 3));
            director.DesignationAppointmentDate = desigDate;

            if (!DateNormalizer.TryParse(Cell(row, 4), out var origDate))
                Warn(result, r, "OriginalAppointmentDate", Cell(row, 4));
            director.OriginalAppointmentDate = origDate;

            if (!DateNormalizer.TryParse(Cell(row, 5), out var cessDate))
                Warn(result, r, "CessationDate", Cell(row, 5));
            director.CessationDate = cessDate;

            var flags = Cell(row, 6)?.ToString()?.Trim();
            director.Flags = flags is "-" or "" ? null : flags;

            result.Items.Add(director);
        }

        return result;
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;

    private static void Warn(ParseResult<Director> result, int rowIndex, string field, object? raw) =>
        result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, field, raw?.ToString(),
            "BAD_DATE", $"Could not parse {field} '{raw}'", rowIndex + 1));
}
