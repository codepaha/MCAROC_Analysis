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
/// assumed at a fixed index.
///
/// Both data loops stop (not merely skip) at the first table-boundary signal — a blank row, a repeated
/// header (either shape), or a "Total"/footer line — so a footer, a repeated header, or an unrelated
/// row can never be silently ingested as a rating (Codex review, PR #91).</summary>
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
            var headerRow = FindRow(creditRatingsSheet, r => IsAcceptedHeaderRow(creditRatingsSheet.Rows[r]), 0);
            if (headerRow is null)
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                    "CREDIT_RATINGS_HEADER_NOT_FOUND", "Could not locate the full 'Credit Ratings' header row."));
            }
            else
            {
                var r = headerRow.Value + 1;
                for (; r < creditRatingsSheet.Rows.Count; r++)
                {
                    var row = creditRatingsSheet.Rows[r];
                    if (IsAcceptedHeaderRow(row) || IsUnacceptedHeaderRow(row)) break; // a repeated header ends this table
                    var agency = Cell(row, 0);
                    if (string.IsNullOrEmpty(agency)) break; // a blank row ends the table — never scan past it
                    if (IsFooterRow(agency)) break;
                    if (string.Equals(agency, UnacceptedBanner, StringComparison.OrdinalIgnoreCase)) break; // embedded sub-section

                    result.Items.Add(ParseAcceptedRow(creditRatingsSheet, row, r, agency, requestId, ingestionRunId, sourceDocumentId));
                }

                // An "UNACCEPTED RATINGS" banner embedded in the same sheet, further down.
                var embeddedBanner = FindRow(creditRatingsSheet, i => Cell(creditRatingsSheet.Rows[i], 0) is { } c
                    && string.Equals(c, UnacceptedBanner, StringComparison.OrdinalIgnoreCase), r);
                if (embeddedBanner is { } eb)
                {
                    var embeddedHeader = FindRow(creditRatingsSheet, i => IsUnacceptedHeaderRow(creditRatingsSheet.Rows[i]), eb + 1);
                    if (embeddedHeader is { } eh)
                        ParseUnacceptedRows(creditRatingsSheet, eh, requestId, ingestionRunId, sourceDocumentId, result);
                }
            }
        }

        if (unacceptedSheet is not null)
        {
            var headerRow = FindRow(unacceptedSheet, r => IsUnacceptedHeaderRow(unacceptedSheet.Rows[r]), 0);
            if (headerRow is null)
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                    "UNACCEPTED_RATINGS_HEADER_NOT_FOUND", "Could not locate the full 'Unaccepted Ratings' header row."));
            else
                ParseUnacceptedRows(unacceptedSheet, headerRow.Value, requestId, ingestionRunId, sourceDocumentId, result);
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
            if (IsAcceptedHeaderRow(row) || IsUnacceptedHeaderRow(row)) break; // a repeated header ends this table
            var agency = Cell(row, 0);
            if (string.IsNullOrEmpty(agency)) break;
            if (IsFooterRow(agency)) break;

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

    /// <summary>Validates the full 9-column accepted-ratings header shape (not just "AGENCY" in column
    /// 0, which can't distinguish this layout from the 7-column unaccepted one, let alone from an
    /// unrelated row). Tolerates the trailing-space "AGENCY " variant observed in some exports — the
    /// shared <see cref="Cell"/> helper already trims.</summary>
    private static bool IsAcceptedHeaderRow(IReadOnlyList<object?> row) =>
        CellEquals(row, 0, "AGENCY") && CellEquals(row, 1, "DATE") && CellEquals(row, 2, "INSTRUMENT") &&
        CellEquals(row, 3, "AMOUNT") && CellEquals(row, 4, "CURRENCY") && CellEquals(row, 5, "RATING") &&
        CellEquals(row, 6, "ACTION") && CellEquals(row, 7, "OUTLOOK");

    /// <summary>Validates the full 7-column unaccepted-ratings header shape.</summary>
    private static bool IsUnacceptedHeaderRow(IReadOnlyList<object?> row) =>
        CellEquals(row, 0, "AGENCY") && CellEquals(row, 1, "INSTRUMENT") && CellEquals(row, 2, "AMOUNT") &&
        CellEquals(row, 3, "CURRENCY") && CellEquals(row, 4, "RATING") &&
        (Cell(row, 5)?.StartsWith("DATE OF NON", StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool IsFooterRow(string agency) =>
        agency.Equals("Total", StringComparison.OrdinalIgnoreCase) ||
        agency.StartsWith("Grand Total", StringComparison.OrdinalIgnoreCase);

    private static bool CellEquals(IReadOnlyList<object?> row, int i, string expected) =>
        string.Equals(Cell(row, i), expected, StringComparison.OrdinalIgnoreCase);

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
