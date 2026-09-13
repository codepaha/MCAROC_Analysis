using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Auditors' Comments-Standalone", which actually stacks two sub-tables (confirmed by
/// inspecting every column of the full sheet, not just its first few rows/columns):
///   1. row 0 = title, row 1 = header, rows 2.. = one row per year: Financial Year, Qualified/Adverse Y-N,
///      (blank), (blank), Comments Given By — ends at the first blank row.
///   2. A second header "Serial Number, Financial Year, Section, Section Name, Auditors' Comments,
///      Directors' Comments, Footnotes" further down (7 columns — the comment text is column 4, NOT
///      column 2, which holds a numeric section code like 700600 that looks like a plausible-but-wrong
///      value if you only spot-check a couple of columns), with one row per individual numbered
///      observation/note.
/// Naively continuing table 1's column mapping into table 2 silently corrupts FinancialYear with the
/// serial number — this parser stops at the blank row and switches column mapping for table 2.</summary>
public static class AuditorsParser
{
    /// <summary>Matches "NAME (Membership Number: X) of FIRM (Registration Number: Y)", with an optional
    /// leading "-" bullet and trailing period, e.g. "- MANAS KUMAR MANIA (Membership Number: 300113) of
    /// U K MAHAPATRA &amp; CO (Registration Number: 320039E)."</summary>
    private static readonly Regex AuditorIdentityPattern = new(
        @"^-?\s*(?<name>.+?)\s*\(Membership Number:\s*(?<membership>[^)]+)\)\s+of\s+(?<firm>.+?)\s*\(Registration Number:\s*(?<frn>[^)]+)\)\.?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Also parses the identically-structured "Auditors' Comments-Consolidated" sheet — pass
    /// <paramref name="basis"/> = Consolidated.</summary>
    public static ParseResult<AuditorObservation> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        FinancialBasis basis = FinancialBasis.Standalone)
    {
        var result = new ParseResult<AuditorObservation>();

        var r = 2;
        for (; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (!AmountNormalizer.TryParse(Cell(row, 0), out var yearValue, out _) || yearValue is null)
                break; // blank row or non-year row ends table 1

            var qualified = Cell(row, 1)?.ToString()?.Trim();
            var commentsBy = Cell(row, 4)?.ToString()?.Trim();

            var observation = new AuditorObservation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                FinancialYear = (int)yearValue.Value,
                Basis = basis,
                HasQualificationOrAdverseRemark = qualified?.Equals("Yes", StringComparison.OrdinalIgnoreCase) == true,
                AuditorName = commentsBy,
                ObservationText = commentsBy
            };

            var identity = commentsBy is null ? null : AuditorIdentityPattern.Match(commentsBy);
            if (identity is { Success: true })
            {
                observation.AuditorName = identity.Groups["name"].Value.Trim();
                observation.MembershipNumber = identity.Groups["membership"].Value.Trim();
                observation.FirmName = identity.Groups["firm"].Value.Trim();
                observation.FirmRegistrationNumber = identity.Groups["frn"].Value.Trim();
            }

            result.Items.Add(observation);
        }

        var detailHeaderRow = -1;
        for (; r < sheet.Rows.Count; r++)
        {
            if (Cell(sheet.Rows[r], 0)?.ToString()?.Trim() == "Serial Number")
            {
                detailHeaderRow = r;
                break;
            }
        }
        if (detailHeaderRow < 0) return result;

        for (r = detailHeaderRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            if (!AmountNormalizer.TryParse(Cell(row, 1), out var yearValue, out _) || yearValue is null) continue;

            var observationText = NormalizeDetailText(Cell(row, 4));
            var directorsComments = NormalizeDetailText(Cell(row, 5));
            var footnotes = NormalizeDetailText(Cell(row, 6));
            if (observationText is null && directorsComments is null && footnotes is null) continue; // nothing on this row at all

            int? serialNumber = null;
            if (AmountNormalizer.TryParse(Cell(row, 0), out var serial, out _) && serial is not null)
            {
                if (serial.Value == decimal.Truncate(serial.Value))
                {
                    serialNumber = (int)serial.Value;
                }
                else
                {
                    result.AddWarning(new ParseIssue(IssueSeverity.Warning, nameof(AuditorsParser), "SerialNumber",
                        serial.Value.ToString(CultureInfo.InvariantCulture), "AUDITOR_SERIAL_NUMBER_NOT_INTEGRAL",
                        $"Serial Number '{serial.Value}' is not a whole number — left unset rather than truncated.", r + 1));
                }
            }

            result.Items.Add(new AuditorObservation
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                FinancialYear = (int)yearValue.Value,
                Basis = basis,
                ObservationText = observationText,
                SerialNumber = serialNumber,
                SectionCode = NormalizeDetailText(Cell(row, 2)),
                SectionName = NormalizeDetailText(Cell(row, 3)),
                DirectorsComments = directorsComments,
                Footnotes = footnotes
            });
        }

        return result;
    }

    /// <summary>Shared "blank / "-" / NIL means null" normalization for every free-text detail-table
    /// column (Section, Section Name, Auditors' Comments, Directors' Comments, Footnotes) — applied
    /// uniformly so no column gets a bespoke rule.</summary>
    private static string? NormalizeDetailText(object? cell)
    {
        var text = cell?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) || text == "-" || text.Equals("NIL", StringComparison.OrdinalIgnoreCase)
            ? null : text;
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;
}
