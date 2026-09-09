using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

public record GstParseOutput(ParseResult<GstRegistration> Registrations, ParseResult<GstFiling> Filings);

/// <summary>Parses "GST" (registration-level info, one or more rows per GSTIN — deduplicated to one
/// GstRegistration per GSTIN) and "Annexure - GST" (per-period filing history). Filings are attached to
/// their parent's navigation collection so EF fixes up the foreign key on save rather than us tracking
/// generated ids by hand.</summary>
public static class GstParser
{
    public static GstParseOutput Parse(
        SheetData? gstSheet, SheetData? annexureSheet,
        long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var regResult = new ParseResult<GstRegistration>();
        var filingResult = new ParseResult<GstFiling>();
        var byGstin = new Dictionary<string, GstRegistration>();

        if (gstSheet is not null)
        {
            for (var r = 1; r < gstSheet.Rows.Count; r++)
            {
                var row = gstSheet.Rows[r];
                var gstin = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
                if (string.IsNullOrEmpty(gstin)) continue;

                if (byGstin.ContainsKey(gstin))
                {
                    // The GST sheet carries one row per (GSTIN, return type) — the same GSTIN can appear
                    // several times with different return-type / tax-period / latest-filing columns.
                    // Registration identity is per-GSTIN, so we keep one GstRegistration, but the extra
                    // row is not dropped silently: it is flagged here and captured verbatim in SourceRows.
                    // The authoritative per-period filing history is the "Annexure - GST" sheet.
                    regResult.AddWarning(new ParseIssue(IssueSeverity.Warning, nameof(GstParser), "Gstin", gstin,
                        "GST_ADDITIONAL_REGISTRATION_ROW",
                        $"GSTIN '{gstin}' has an additional GST-sheet row (return type '{Cell(row, 3)}', " +
                        $"tax period '{Cell(row, 6)}') beyond the one kept as the registration — see the raw " +
                        "source row and the GST filing annexure.", r + 1));
                    continue;
                }

                var reg = new GstRegistration
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = gstSheet.Name,
                    SourceRowNumber = r + 1,
                    Gstin = gstin,
                    Status = Cell(row, 1),
                    State = Cell(row, 2),
                    TaxpayerType = Cell(row, 10),
                    TradeName = Cell(row, 12),
                    NatureOfBusinessActivities = Cell(row, 13),
                    Flags = Cell(row, 14)
                };
                if (DateNormalizer.TryParse(row.Count > 7 ? row[7] : null, out var regDate)) reg.RegistrationDate = regDate;

                byGstin[gstin] = reg;
                regResult.Items.Add(reg);
            }
        }

        if (annexureSheet is not null)
        {
            for (var r = 1; r < annexureSheet.Rows.Count; r++)
            {
                var row = annexureSheet.Rows[r];
                var gstin = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
                if (string.IsNullOrEmpty(gstin)) continue;

                var filing = new GstFiling
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = annexureSheet.Name,
                    SourceRowNumber = r + 1,
                    Gstin = gstin,
                    ReturnType = Cell(row, 1) ?? string.Empty,
                    FinancialYear = Cell(row, 2),
                    TaxPeriod = Cell(row, 3),
                    FilingStatus = Cell(row, 6)
                };
                if (DateNormalizer.TryParse(row.Count > 4 ? row[4] : null, out var due)) filing.DueDate = due;
                if (DateNormalizer.TryParse(row.Count > 5 ? row[5] : null, out var filed)) filing.FilingDate = filed;
                if (filing.DueDate is not null && filing.FilingDate is not null)
                    filing.DelayDays = filing.FilingDate.Value.DayNumber - filing.DueDate.Value.DayNumber;

                if (byGstin.TryGetValue(gstin, out var parent))
                    parent.Filings.Add(filing);
                else
                    filingResult.AddWarning(new ParseIssue(IssueSeverity.Warning, nameof(GstParser), "Gstin", gstin,
                        "ORPHAN_FILING", $"Filing references GSTIN '{gstin}' not present in the GST registration sheet", r + 1));

                filingResult.Items.Add(filing);
            }
        }

        return new GstParseOutput(regResult, filingResult);
    }

    private static string? Cell(IReadOnlyList<object?> row, int index)
    {
        var text = index < row.Count ? row[index]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
