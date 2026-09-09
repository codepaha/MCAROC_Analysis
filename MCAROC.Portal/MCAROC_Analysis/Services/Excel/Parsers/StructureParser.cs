using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "SHARE HOLDING SUMMARY" key/value block at the top of the "Structure" sheet
/// (promoter/public split, shareholder counts, total shares). One CompanyStructure per run. Per-year
/// promoter/public share tables further down the sheet are not extracted this phase.</summary>
public static class StructureParser
{
    private const string ParserName = nameof(StructureParser);

    public static ParseResult<CompanyStructure> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<CompanyStructure>();
        var s = new CompanyStructure
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = 1
        };

        var any = false;
        foreach (var row in sheet.Rows)
        {
            var label = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (string.IsNullOrEmpty(label)) continue;
            var value = row.Count > 1 ? row[1] : null;

            switch (label.ToUpperInvariant())
            {
                case "PROMOTER %":
                    if (AmountNormalizer.TryParse(value, out var p, out _)) { s.PromoterHoldingPercent = p; any = true; }
                    break;
                case "PUBLIC %":
                    if (AmountNormalizer.TryParse(value, out var pub, out _)) { s.PublicHoldingPercent = pub; any = true; }
                    break;
                case "NO. OF SHAREHOLDERS":
                    if (AmountNormalizer.TryParse(value, out var n, out _) && n is { } nv) { s.TotalShareholders = (int)nv; any = true; }
                    break;
                case "NO. OF PROMOTER SHAREHOLDERS":
                    if (AmountNormalizer.TryParse(value, out var pn, out _) && pn is { } pnv) { s.PromoterShareholders = (int)pnv; any = true; }
                    break;
                case "TOTAL EQUITY SHARES":
                    if (AmountNormalizer.TryParse(value, out var eq, out _) && eq is { } eqv) { s.TotalEquityShares = (long)eqv; any = true; }
                    break;
                case "TOTAL PREFERENCE SHARES":
                    if (AmountNormalizer.TryParse(value, out var pr, out _) && pr is { } prv) { s.TotalPreferenceShares = (long)prv; any = true; }
                    break;
            }
        }

        if (!any)
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "STRUCTURE_SUMMARY_EMPTY", "No SHARE HOLDING SUMMARY values were found on the Structure sheet."));
        else
            result.Items.Add(s);

        return result;
    }
}
