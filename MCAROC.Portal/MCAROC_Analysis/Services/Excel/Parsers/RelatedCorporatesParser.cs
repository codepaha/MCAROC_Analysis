using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses the "Related Corporates" sheet — subsidiaries / associates / JVs, one row per
/// (corporate, financial year). RelationshipType is normalized from the sheet's explicit Relationship
/// text only ("SUBSIDIARY CORPORATES" / "ASSOCIATE CORPORATES" / "JOINT VENTURE"); anything unrecognised
/// stays Other. This sheet carries no CIN column, so Cin is left null.</summary>
public static class RelatedCorporatesParser
{
    private const string ParserName = nameof(RelatedCorporatesParser);

    // header row 1: FY Ending | Corporate Name | Relationship | Corporate Type | Shareholding (%) |
    //               Country/City | Paid Up Capital | Sum Of Charges | Date Of Incorporation |
    //               Company/LLP Status | Active Compliance | Remarks
    public static ParseResult<RelatedCorporate> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var result = new ParseResult<RelatedCorporate>();

        var headerRow = FindHeaderRow(sheet);
        if (headerRow < 0)
        {
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "RELATED_CORP_HEADER_NOT_FOUND", "Could not locate the 'Corporate Name' header row."));
            return result;
        }

        for (var r = headerRow + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var name = Cell(row, 1);
            if (string.IsNullOrEmpty(name)) continue;

            var rc = new RelatedCorporate
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                EntityNameRaw = name,
                EntityNameNormalized = NameNormalizer.Normalize(name),
                RelationshipRaw = Cell(row, 2),
                RelationshipType = NormalizeRelationship(Cell(row, 2)),
                CorporateType = Cell(row, 3),
                Location = Cell(row, 5),
                CompanyStatus = Cell(row, 9),
                ActiveCompliance = Cell(row, 10),
                Remarks = Cell(row, 11)
            };
            if (DateNormalizer.TryParse(row.Count > 0 ? row[0] : null, out var fye)) rc.FinancialYearEnding = fye;
            if (AmountNormalizer.TryParse(CellRaw(row, 4), out var hp, out _)) rc.HoldingPercent = hp;
            if (AmountNormalizer.TryParse(CellRaw(row, 6), out var puc, out _)) rc.PaidUpCapitalCrore = puc;
            if (AmountNormalizer.TryParse(CellRaw(row, 7), out var soc, out _)) rc.SumOfChargesCrore = soc;
            if (DateNormalizer.TryParse(row.Count > 8 ? row[8] : null, out var doi)) rc.DateOfIncorporation = doi;

            result.Items.Add(rc);
        }

        return result;
    }

    private static int FindHeaderRow(SheetData sheet)
    {
        for (var r = 0; r < Math.Min(sheet.Rows.Count, 6); r++)
        {
            var c1 = sheet.Rows[r].Count > 1 ? sheet.Rows[r][1]?.ToString()?.Trim() : null;
            if (string.Equals(c1, "Corporate Name", StringComparison.OrdinalIgnoreCase))
                return r;
        }
        return -1;
    }

    private static RelationshipType NormalizeRelationship(string? raw)
    {
        var t = raw?.ToUpperInvariant() ?? "";
        if (t.Contains("SUBSIDIARY")) return RelationshipType.Subsidiary;
        if (t.Contains("ASSOCIATE")) return RelationshipType.Associate;
        if (t.Contains("JOINT VENTURE") || t.Contains("JV")) return RelationshipType.JointVenture;
        if (t.Contains("HOLDING")) return RelationshipType.Holding;
        return RelationshipType.Other;
    }

    private static object? CellRaw(IReadOnlyList<object?> row, int i) => i < row.Count ? row[i] : null;

    private static string? Cell(IReadOnlyList<object?> row, int i)
    {
        var text = i < row.Count ? row[i]?.ToString()?.Trim() : null;
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }
}
