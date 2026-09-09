using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Compliance" sheet, which stacks several titled sections in one column grid:
/// name removal / restoration, BIFR, CDR (corporate debt restructuring), and CIBIL SUIT FILED CASES
/// (bank / amount / defaulter type). "This corporate has no ..." / "As per our records ..." statements
/// produce no record. Section detection is by the all-caps title rows.</summary>
public static class ComplianceParser
{
    private const string ParserName = nameof(ComplianceParser);

    private enum Section { None, NameRemoval, Bifr, Cdr, SuitFiled }

    public static ParseResult<ComplianceRecord> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<ComplianceRecord>();
        var section = Section.None;

        ComplianceRecord New(int rowNumber) => new()
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = rowNumber
        };

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var c0 = Cell(row, 0);
            if (c0 is null) continue;

            var upper = c0.ToUpperInvariant();

            // Section headers.
            if (upper.StartsWith("INCIDENTS OF NAME REMOVAL")) { section = Section.NameRemoval; continue; }
            if (upper == "BIFR") { section = Section.Bifr; continue; }
            if (upper == "CDR") { section = Section.Cdr; continue; }
            if (upper.StartsWith("SUIT FILED")) { section = Section.SuitFiled; continue; }

            // Column-header rows within a section — skip.
            if (upper is "STRUCK OFF STATUS" or "CASE NO." or "DESCRIPTION" or "SOURCE") continue;
            if (upper.StartsWith("CASE NO.")) continue;

            // "no records" / narrative statements — informational, not a record.
            if (upper.Contains("HAS NO ") || upper.StartsWith("AS PER OUR RECORDS")) continue;

            switch (section)
            {
                case Section.NameRemoval:
                {
                    var rec = New(r + 1);
                    rec.RecordType = upper.Contains("RESTOR") ? ComplianceRecordType.NameRestoration : ComplianceRecordType.NameRemoval;
                    rec.Status = c0;
                    rec.SourceText = JoinRow(row);
                    result.Items.Add(rec);
                    break;
                }
                case Section.Bifr:
                {
                    var rec = New(r + 1);
                    rec.RecordType = ComplianceRecordType.Bifr;
                    rec.Description = c0;                      // Case No.
                    rec.Status = Cell(row, 1);
                    if (DateNormalizer.TryParse(row.Count > 2 ? row[2] : null, out var d)) rec.RecordDate = d;
                    rec.SourceText = JoinRow(row);
                    result.Items.Add(rec);
                    break;
                }
                case Section.Cdr:
                {
                    var rec = New(r + 1);
                    rec.RecordType = ComplianceRecordType.Cdr;
                    rec.Description = c0;
                    if (DateNormalizer.TryParse(row.Count > 1 ? row[1] : null, out var d)) rec.RecordDate = d;
                    rec.SourceText = JoinRow(row);
                    result.Items.Add(rec);
                    break;
                }
                case Section.SuitFiled:
                {
                    var rec = New(r + 1);
                    rec.RecordType = ComplianceRecordType.SuitFiled;
                    rec.Source = c0;                          // "CIBIL"
                    rec.Bank = Cell(row, 1);
                    if (DateNormalizer.TryParse(row.Count > 2 ? row[2] : null, out var d)) rec.RecordDate = d;
                    if (AmountNormalizer.TryParse(row.Count > 3 ? row[3] : null, out var amt, out _)) rec.AmountCrore = amt;
                    rec.DefaulterType = Cell(row, 4);
                    rec.Description = rec.Bank;
                    rec.SourceText = JoinRow(row);

                    // Keep EVERY reported quarter. CIBIL re-reports the same suit-filed default every
                    // quarter, but that quarter-by-quarter history is itself material to a lender — the
                    // dossier's summary view collapses by (bank, defaulter type, amount) for display; the
                    // record set stays complete.
                    result.Items.Add(rec);
                    break;
                }
            }
        }

        return result;
    }

    private static string JoinRow(IReadOnlyList<object?> row) =>
        string.Join(" | ", row.Select(c => c?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s)));

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
