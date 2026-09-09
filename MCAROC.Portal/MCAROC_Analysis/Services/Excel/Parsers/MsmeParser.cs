using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "MSME Supplier Payment Delays": row 0 has the reporting period in column 1, then a
/// blank row, then the "Supplier Name / PAN / Amount Due" header, then data.</summary>
public static class MsmeParser
{
    public static ParseResult<MsmePayment> Parse(SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<MsmePayment>();

        var reportingPeriod = sheet.Rows.Count > 0 && sheet.Rows[0].Count > 1
            ? sheet.Rows[0][1]?.ToString()?.Trim() ?? string.Empty
            : string.Empty;

        var headerRow = -1;
        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var col0 = sheet.Rows[r].Count > 0 ? sheet.Rows[r][0]?.ToString()?.Trim() : null;
            if (col0 == "Supplier Name") { headerRow = r; break; }
        }
        if (headerRow < 0) return result;

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var supplier = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(supplier)) continue;

            var pan = row.Count > 1 ? row[1]?.ToString()?.Trim() : null;
            AmountNormalizer.TryParse(row.Count > 2 ? row[2] : null, out var amount, out _);

            result.Items.Add(new MsmePayment
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                ReportingPeriod = reportingPeriod,
                SupplierNameRaw = supplier,
                SupplierPan = pan is "-" or "" ? null : pan,
                AmountDueCrore = amount
            });
        }

        return result;
    }
}
