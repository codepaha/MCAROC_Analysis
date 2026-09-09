using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Other Directorships": row 0 is the header, data from row 1.
/// DIRECTOR NAME arrives as "AMITABH SARAN (DIN : 00415231)" — split via NameNormalizer.SplitDin.</summary>
public static class OtherDirectorshipsParser
{
    private const string ParserName = nameof(OtherDirectorshipsParser);

    public static ParseResult<DirectorAssociation> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<DirectorAssociation>();

        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var directorRaw = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            var corporateName = row.Count > 1 ? row[1]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(directorRaw) || string.IsNullOrEmpty(corporateName)) continue;

            var (directorName, din) = NameNormalizer.SplitDin(directorRaw);

            var assoc = new DirectorAssociation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                DirectorNameRaw = directorName,
                DirectorDin = din ?? string.Empty,
                ConnectedCompanyRaw = corporateName,
                ConnectedCompanyNormalized = NameNormalizer.Normalize(corporateName),
                ConnectedCin = NullIfDash(Cell(row, 2)),
                CorporateType = NullIfDash(Cell(row, 3))
            };

            if (AmountNormalizer.TryParse(Cell(row, 4), out var paidUp, out _)) assoc.PaidUpCapitalCrore = paidUp;
            if (AmountNormalizer.TryParse(Cell(row, 5), out var charges, out _)) assoc.SumOfChargesCrore = charges;
            if (DateNormalizer.TryParse(Cell(row, 6), out var incDate)) assoc.DateOfIncorporation = incDate;

            assoc.CompanyStatus = NullIfDash(Cell(row, 7));
            assoc.ActiveCompliance = NullIfDash(Cell(row, 8));

            if (DateNormalizer.TryParse(Cell(row, 9), out var apptDate)) assoc.AppointmentDate = apptDate;
            if (DateNormalizer.TryParse(Cell(row, 10), out var cessDate)) assoc.CessationDate = cessDate;

            if (string.IsNullOrEmpty(din))
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "DirectorDin", directorRaw,
                    "MISSING_DIN", $"Could not extract DIN from director name '{directorRaw}'", r + 1));

            result.Items.Add(assoc);
        }

        return result;
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;

    private static string? NullIfDash(object? value)
    {
        var text = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
