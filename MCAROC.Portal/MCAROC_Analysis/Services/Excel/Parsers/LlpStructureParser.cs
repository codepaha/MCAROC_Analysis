using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses an LLP's "Structure" sheet (#539), which has nothing of a company's shareholding grids: CONTRIBUTION BY PARTNERS (Name,
/// Designation, Obligation of Contribution, Contribution Received → <see cref="LlpContribution"/>), CONTRIBUTION BY BODY CORPORATES (Name,
/// Nominee Name, the same two amounts; "There is no contribution by body corporates." when empty), and SUMMARY OF DESIGNATED PARTNER(S) /
/// PARTNER(S) — counts by category whose "Total" row gives the partners and the designated partners (→ <see cref="CompanyStructure"/>).
/// Each block's header carries its "as on" date ("CONTRIBUTION BY PARTNERS - 31 Mar, 2026").</summary>
public static class LlpStructureParser
{
    private const string ParserName = nameof(LlpStructureParser);

    /// <summary>The sheet is an LLP's when its first block is a contribution grid, not a company's share holding summary.</summary>
    public static bool IsLlpStructure(SheetData sheet) =>
        sheet.Rows.Select(r => Cell(r, 0)).FirstOrDefault(c => c.Length > 0)?.StartsWith("CONTRIBUTION BY", StringComparison.OrdinalIgnoreCase) == true;

    public static ParseResult<CompanyStructure> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId, out List<LlpContribution> contributions)
    {
        var result = new ParseResult<CompanyStructure>();
        contributions = [];
        LlpContributorKind? kind = null;
        DateOnly? asOn = null;
        var inSummary = false;
        var order = 0;
        int? partners = null, designated = null;

        for (var r = 0; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var c0 = Cell(row, 0);
            if (c0.Length == 0) continue;

            if (c0.StartsWith("CONTRIBUTION BY PARTNERS", StringComparison.OrdinalIgnoreCase)
                || c0.StartsWith("CONTRIBUTION BY BODY CORPORATE", StringComparison.OrdinalIgnoreCase))
            {
                kind = c0.Contains("BODY CORPORATE", StringComparison.OrdinalIgnoreCase) ? LlpContributorKind.BodyCorporate : LlpContributorKind.Partner;
                asOn = TrailingDate(c0);
                inSummary = false;
                order = 0;
                continue;
            }
            if (c0.StartsWith("SUMMARY OF DESIGNATED PARTNER", StringComparison.OrdinalIgnoreCase))
            {
                kind = null;
                inSummary = true;
                continue;
            }

            if (inSummary)
            {
                // "Total | partners | designated (resident) | designated (others) | total": partners are column 1, designated partners 2 + 3.
                if (c0.Equals("Total", StringComparison.OrdinalIgnoreCase))
                {
                    partners = Count(row, 1);
                    designated = Count(row, 2) is { } resident ? resident + (Count(row, 3) ?? 0) : Count(row, 3);
                }
                continue;
            }

            if (kind is not { } k) continue;
            if (c0.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;                                   // column header
            if (c0.StartsWith("There is no contribution", StringComparison.OrdinalIgnoreCase)) continue;          // empty-state note

            var second = Cell(row, 1);
            contributions.Add(new LlpContribution
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                Kind = k,
                AsOnDate = asOn,
                Name = c0,
                Designation = k == LlpContributorKind.Partner && second.Length > 0 ? second : null,
                NomineeName = k == LlpContributorKind.BodyCorporate && second.Length > 0 && second != "-" ? second : null,
                ObligationOfContributionCrore = Amount(row, 2, r, result),
                ContributionReceivedCrore = Amount(row, 3, r, result),
                DisplayOrder = ++order,
            });
        }

        if (contributions.Count == 0 && partners is null && designated is null)
            result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, null, null,
                "LLP_STRUCTURE_EMPTY", "No partner contribution or partner summary was found on the LLP's Structure sheet."));

        result.Items.Add(new CompanyStructure
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            SourceSheetName = sheet.Name,
            SourceRowNumber = 1,
            LlpPartners = partners,
            LlpDesignatedPartners = designated,
        });
        return result;
    }

    private static decimal? Amount(IReadOnlyList<object?> row, int col, int r, ParseResult<CompanyStructure> result)
    {
        if (col >= row.Count) return null;
        if (AmountNormalizer.TryParse(row[col], out var value, out var raw)) return value;
        result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "Contribution", raw, "BAD_AMOUNT", $"Could not parse contribution '{raw}'", r + 1));
        return null;
    }

    private static int? Count(IReadOnlyList<object?> row, int col) =>
        col < row.Count && AmountNormalizer.TryParse(row[col], out var v, out _) && v is { } d ? (int)Math.Round(d) : null;

    private static string Cell(IReadOnlyList<object?> row, int col) => col < row.Count ? row[col]?.ToString()?.Trim() ?? "" : "";

    private static DateOnly? TrailingDate(string header)
    {
        var dash = header.LastIndexOf(" - ", StringComparison.Ordinal);
        return dash >= 0 && DateNormalizer.TryParse(header[(dash + 3)..].Trim(), out var d) ? d : null;
    }
}
