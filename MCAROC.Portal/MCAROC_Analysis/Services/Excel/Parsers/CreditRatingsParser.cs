using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Credit Ratings" (header row 0, 9 cols: AGENCY · DATE · INSTRUMENT · AMOUNT ·
/// CURRENCY · RATING · ACTION · OUTLOOK · REMARKS — IsAccepted=true) and "Unaccepted Ratings" (header
/// row 0, 7 cols: AGENCY · INSTRUMENT · AMOUNT · CURRENCY · RATING · DATE OF NON-ACCEPTANCE · REMARKS —
/// IsAccepted=false). Per the A9/#51 issue, some exports fold the "Unaccepted Ratings" rows into a
/// second banner+header block further down the *Credit Ratings* sheet itself rather than a separate
/// sheet — <see cref="Parse"/> checks for that banner after the accepted table ends, in addition to a
/// standalone <paramref name="unacceptedSheet"/>. Both sheets are absent from the COASTAL fixture
/// (9/41 and 2/41 of the wider portfolio set carry them respectively), so this parser has not been
/// verified against a real workbook — column layout is exactly the issue's spec, header row is located
/// dynamically (tolerates the trailing-space header observed in some exports, "AGENCY ") rather than
/// assumed at a fixed index.</summary>
public static class CreditRatingsParser
{
    private const string ParserName = nameof(CreditRatingsParser);
    private const string UnacceptedBanner = "UNACCEPTED RATINGS";

    public static ParseResult<CreditRating> Parse(
        SheetData? creditRatingsSheet, SheetData? unacceptedSheet,
        long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<CreditRating>();

        if (creditRatingsSheet is not null)
        {
            var headerRow = FindHeaderRow(creditRatingsSheet);
            if (headerRow < 0)
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                    "CREDIT_RATINGS_HEADER_NOT_FOUND", "Could not locate the 'AGENCY' header row on the Credit Ratings sheet."));
            }
            else
            {
                var r = headerRow + 1;
                for (; r < creditRatingsSheet.Rows.Count; r++)
                {
                    var row = creditRatingsSheet.Rows[r];
                    var agency = Cell(row, 0);
                    if (string.IsNullOrEmpty(agency)) break; // blank row ends the accepted table
                    if (string.Equals(agency, UnacceptedBanner, StringComparison.OrdinalIgnoreCase)) break; // embedded sub-section

                    result.Items.Add(ParseAcceptedRow(creditRatingsSheet, row, r, agency, requestId, ingestionRunId, sourceDocumentId));
                }

                // An "UNACCEPTED RATINGS" banner embedded in the same sheet, further down.
                var embeddedBanner = FindRow(creditRatingsSheet, i => Cell(creditRatingsSheet.Rows[i], 0) is { } c
                    && string.Equals(c, UnacceptedBanner, StringComparison.OrdinalIgnoreCase), r);
                if (embeddedBanner is { } eb)
                {
                    var embeddedHeader = FindHeaderRow(creditRatingsSheet, eb + 1);
                    if (embeddedHeader >= 0)
                        ParseUnacceptedRows(creditRatingsSheet, embeddedHeader, requestId, ingestionRunId, sourceDocumentId, result);
                }
            }
        }

        if (unacceptedSheet is not null)
        {
            var headerRow = FindHeaderRow(unacceptedSheet);
            if (headerRow < 0)
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                    "UNACCEPTED_RATINGS_HEADER_NOT_FOUND", "Could not locate the 'AGENCY' header row on the Unaccepted Ratings sheet."));
            else
                ParseUnacceptedRows(unacceptedSheet, headerRow, requestId, ingestionRunId, sourceDocumentId, result);
        }

        return result;
    }

    private static CreditRating ParseAcceptedRow(
        SheetData sheet, IReadOnlyList<object?> row, int r, string agency,
        long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var cr = new CreditRating
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = r + 1,
            Agency = agency,
            Instrument = Cell(row, 2),
            Currency = Cell(row, 4),
            Rating = Cell(row, 5),
            Action = Cell(row, 6),
            Outlook = Cell(row, 7),
            Remarks = Cell(row, 8),
            IsAccepted = true
        };
        if (DateNormalizer.TryParse(row.Count > 1 ? row[1] : null, out var date)) cr.RatingDate = date;
        if (AmountNormalizer.TryParse(row.Count > 3 ? row[3] : null, out var amt, out _)) cr.Amount = amt;
        return cr;
    }

    private static void ParseUnacceptedRows(
        SheetData sheet, int headerRow, long requestId, long ingestionRunId, long? sourceDocumentId, ParseResult<CreditRating> result)
    {
        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var agency = Cell(row, 0);
            if (string.IsNullOrEmpty(agency)) break;

            var cr = new CreditRating
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                Agency = agency,
                Instrument = Cell(row, 1),
                Currency = Cell(row, 3),
                Rating = Cell(row, 4),
                Remarks = Cell(row, 6),
                IsAccepted = false
            };
            if (AmountNormalizer.TryParse(row.Count > 2 ? row[2] : null, out var amt, out _)) cr.Amount = amt;
            if (DateNormalizer.TryParse(row.Count > 5 ? row[5] : null, out var date)) cr.RatingDate = date;
            result.Items.Add(cr);
        }
    }

    /// <summary>Locates the "AGENCY" header row, tolerating a trailing-space variant ("AGENCY ")
    /// observed in some exports.</summary>
    private static int FindHeaderRow(SheetData sheet, int startAt = 0) =>
        FindRow(sheet, r => string.Equals(Cell(sheet.Rows[r], 0), "AGENCY", StringComparison.OrdinalIgnoreCase), startAt) ?? -1;

    private static int? FindRow(SheetData sheet, Func<int, bool> predicate, int startAt)
    {
        for (var r = startAt; r < sheet.Rows.Count; r++)
            if (predicate(r))
                return r;
        return null;
    }

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
