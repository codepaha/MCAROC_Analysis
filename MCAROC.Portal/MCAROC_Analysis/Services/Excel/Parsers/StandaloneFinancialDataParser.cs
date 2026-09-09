using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Standalone Financial Data". The sheet stacks three sections (Balance Sheet, P&amp;L, Cash
/// Flow) in one grid. Balance Sheet and P&amp;L share the same year-column header (confirmed by direct
/// inspection). Cash Flow does NOT — the sample data shows it reporting only the most recent N years,
/// left-packed into columns 2..(2+N-1) rather than aligned to the full year list, which we detected by
/// comparing its values against the P&amp;L section's matching row. We map its columns to the last N years
/// of the master year list and log a warning noting that assumption, rather than silently guessing.</summary>
public static class StandaloneFinancialDataParser
{
    private const string ParserName = nameof(StandaloneFinancialDataParser);

    private static readonly Dictionary<string, string> BalanceSheetAndPnlRowToProperty = new()
    {
        ["Share Capital"] = nameof(FinancialYearData.ShareCapital),
        ["Total Equity"] = nameof(FinancialYearData.NetWorth),
        ["Long Term Borrowings"] = nameof(FinancialYearData.LongTermBorrowings),
        ["Short Term Borrowings"] = nameof(FinancialYearData.ShortTermBorrowings),
        ["Trade Payables"] = nameof(FinancialYearData.TradePayables),
        ["Total Current Liabilities"] = nameof(FinancialYearData.CurrentLiabilities),
        ["Inventories"] = nameof(FinancialYearData.Inventory),
        ["Trade Receivables"] = nameof(FinancialYearData.TradeReceivables),
        ["Cash and Bank Balances"] = nameof(FinancialYearData.CashAndBank),
        ["Total Current Assets"] = nameof(FinancialYearData.CurrentAssets),
        ["Net Revenue"] = nameof(FinancialYearData.Revenue),
        ["Other Income"] = nameof(FinancialYearData.OtherIncome),
        ["Operating Profit ( EBITDA )"] = nameof(FinancialYearData.Ebitda),
        ["Profit Before Interest and Tax"] = nameof(FinancialYearData.Ebit),
        ["Finance Costs"] = nameof(FinancialYearData.FinanceCost),
        ["Profit Before Tax"] = nameof(FinancialYearData.Pbt),
        ["Profit for the Period"] = nameof(FinancialYearData.Pat),
    };

    private static readonly Dictionary<string, string> CashFlowRowToProperty = new()
    {
        ["Net Cash Flows from / ( Used in ) Operating Activities"] = nameof(FinancialYearData.Cfo),
        ["Net Cash Flows from / ( Used in ) Investing Activities"] = nameof(FinancialYearData.Cfi),
        ["Net Cash Flows from / ( Used in ) Financing Activities"] = nameof(FinancialYearData.Cff),
    };

    /// <summary>Also parses the identically-structured "Consolidated Financial Data" sheet — pass
    /// <paramref name="basis"/> = Consolidated. Every produced row is stamped with that basis.
    /// <paramref name="facts"/> receives every value-bearing label × year on the sheet that is NOT
    /// mapped to a typed <see cref="FinancialYearData"/> column, so nothing is silently dropped.</summary>
    public static ParseResult<FinancialYearData> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        out List<FinancialFact> facts, FinancialBasis basis = FinancialBasis.Standalone)
    {
        var result = new ParseResult<FinancialYearData>();
        facts = [];
        var factSink = facts;

        void CaptureFacts(IReadOnlyList<object?> row, string label, IReadOnlyList<int?> factYears,
            FinancialStatementSection section, int rowNumber, int columnOffset, bool yearInferred)
        {
            if (IsStructuralRow(label, row, columnOffset)) return; // a repeated "Year" header inside a section, not a fact

            var captured = new List<FinancialFact>();
            for (var i = 0; i < factYears.Count; i++)
            {
                var col = columnOffset + i;
                if (col >= row.Count) continue;
                var raw = row[col]?.ToString()?.Trim();
                if (string.IsNullOrEmpty(raw) || raw == "-") continue;

                AmountNormalizer.TryParse(row[col], out var value, out _);
                captured.Add(new FinancialFact
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    SourceRowNumber = rowNumber + 1,
                    Basis = basis,
                    Section = section,
                    Label = label,
                    FinancialYear = factYears[i],
                    RawValue = raw,
                    NumericValue = value,
                    YearInferred = yearInferred
                });
            }
            if (captured.Count > 0) factSink.AddRange(captured); // skip header/sub-total-only rows
        }

        var headerRowIndex = FindRowIndex(sheet, r => Label(r)?.StartsWith("BALANCE SHEET", StringComparison.OrdinalIgnoreCase) == true);
        if (headerRowIndex is null)
        {
            result.AddError(new ParseIssue(IssueSeverity.Error, ParserName, null, null,
                "MISSING_HEADER", "Could not find 'BALANCE SHEET' header row in Standalone Financial Data"));
            return result;
        }

        var headerRow = sheet.Rows[headerRowIndex.Value];
        var years = new List<int?>();
        for (var c = 2; c < headerRow.Count; c++)
            years.Add(ExtractYear(headerRow[c]?.ToString()));

        var byYear = new Dictionary<int, FinancialYearData>();
        FinancialYearData GetOrCreate(int year)
        {
            if (!byYear.TryGetValue(year, out var entity))
            {
                entity = new FinancialYearData
                {
                    RequestId = requestId,
                    IngestionRunId = ingestionRunId,
                    SourceDocumentId = sourceDocumentId,
                    SourceSheetName = sheet.Name,
                    FinancialYear = year,
                    Basis = basis
                };
                byYear[year] = entity;
            }
            return entity;
        }

        // Balance Sheet + P&L: aligned to the master year header.
        var section = FinancialStatementSection.BalanceSheet;
        for (var r = headerRowIndex.Value + 1; r < sheet.Rows.Count; r++)
        {
            var label = Label(sheet.Rows[r]);
            if (label is null) continue;

            if (label.Equals("CASH FLOW - AOC-4 (Rs. Crore)", StringComparison.OrdinalIgnoreCase))
                break; // switch to the cash-flow handling loop below

            if (label.Contains("PROFIT & LOSS", StringComparison.OrdinalIgnoreCase)
                || label.Contains("PROFIT AND LOSS", StringComparison.OrdinalIgnoreCase)
                || label.Contains("STATEMENT OF PROFIT", StringComparison.OrdinalIgnoreCase))
            {
                section = FinancialStatementSection.ProfitAndLoss; // section sub-header, not a line item
                continue;
            }

            if (BalanceSheetAndPnlRowToProperty.TryGetValue(label, out var property))
                ApplyRow(sheet.Rows[r], years, byYear, property, GetOrCreate, result, r);
            else
                CaptureFacts(sheet.Rows[r], label, years, section, r, columnOffset: 2, yearInferred: false);
        }

        // Cash Flow: locate the section and its own (possibly shorter) populated column range.
        var cashFlowHeaderIndex = FindRowIndex(sheet, r => Label(r)?.StartsWith("CASH FLOW", StringComparison.OrdinalIgnoreCase) == true);
        if (cashFlowHeaderIndex is not null)
        {
            var cfoRowIndex = FindRowIndex(sheet, r => Label(r) == "Net Cash Flows from / ( Used in ) Operating Activities", cashFlowHeaderIndex.Value);
            if (cfoRowIndex is not null)
            {
                var cfoRow = sheet.Rows[cfoRowIndex.Value];
                var populatedCount = 0;
                for (var c = 2; c < cfoRow.Count; c++)
                {
                    if (cfoRow[c] is not null && !string.IsNullOrWhiteSpace(cfoRow[c]!.ToString()))
                        populatedCount = c - 1; // columns are 1-based count from col2
                }

                if (populatedCount > 0 && populatedCount < years.Count)
                {
                    result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "Cfo/Cfi/Cff", null,
                        "CASH_FLOW_YEAR_ALIGNMENT_ASSUMED",
                        $"Cash flow section reports {populatedCount} year(s) vs {years.Count} in the balance sheet/P&L; " +
                        "mapped to the most recent years by column position since the sheet has no separate year header for this section.",
                        cashFlowHeaderIndex.Value + 1));
                }

                var cashFlowInferred = populatedCount > 0 && populatedCount < years.Count;
                var cashFlowYears = populatedCount > 0
                    ? years.Skip(Math.Max(0, years.Count - populatedCount)).ToList()
                    : years;

                for (var r = cashFlowHeaderIndex.Value + 1; r < sheet.Rows.Count; r++)
                {
                    var label = Label(sheet.Rows[r]);
                    if (label is null) continue;
                    if (label.StartsWith("RATIOS", StringComparison.OrdinalIgnoreCase) || label.StartsWith("AUDITOR", StringComparison.OrdinalIgnoreCase))
                        break;

                    if (CashFlowRowToProperty.TryGetValue(label, out var property))
                        ApplyRow(sheet.Rows[r], cashFlowYears, byYear, property, GetOrCreate, result, r, columnOffset: 2);
                    else
                        CaptureFacts(sheet.Rows[r], label, cashFlowYears, FinancialStatementSection.CashFlow, r,
                            columnOffset: 2, yearInferred: cashFlowInferred);
                }

                if (cashFlowInferred)
                    foreach (var e in byYear.Values.Where(e => e.Cfo is not null || e.Cfi is not null || e.Cff is not null))
                        e.CashFlowYearInferred = true;
            }
        }

        // Ratios section — never mapped to a typed column; capture it whole so leverage / liquidity /
        // margin ratios reach the dossier.
        var ratiosHeaderIndex = FindRowIndex(sheet, r => Label(r)?.StartsWith("RATIOS", StringComparison.OrdinalIgnoreCase) == true);
        if (ratiosHeaderIndex is not null)
        {
            for (var r = ratiosHeaderIndex.Value + 1; r < sheet.Rows.Count; r++)
            {
                var label = Label(sheet.Rows[r]);
                if (label is null) continue;
                if (label.StartsWith("AUDITOR", StringComparison.OrdinalIgnoreCase)) break;
                CaptureFacts(sheet.Rows[r], label, years, FinancialStatementSection.Ratios, r, columnOffset: 2, yearInferred: false);
            }
        }

        result.Items.AddRange(byYear.Values.OrderBy(f => f.FinancialYear));
        return result;
    }

    private static void ApplyRow(
        IReadOnlyList<object?> row,
        IReadOnlyList<int?> years,
        Dictionary<int, FinancialYearData> byYear,
        string property,
        Func<int, FinancialYearData> getOrCreate,
        ParseResult<FinancialYearData> result,
        int rowNumber,
        int columnOffset = 2)
    {
        for (var i = 0; i < years.Count; i++)
        {
            var year = years[i];
            if (year is null) continue;

            var col = columnOffset + i;
            if (col >= row.Count) continue;

            if (!AmountNormalizer.TryParse(row[col], out var value, out var raw))
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, property, raw,
                    "BAD_AMOUNT", $"Could not parse amount '{raw}' for {property} FY{year}", rowNumber + 1));
                continue;
            }

            if (value is null) continue;

            var entity = getOrCreate(year.Value);
            typeof(FinancialYearData).GetProperty(property)!.SetValue(entity, value);
        }
    }

    private static readonly string[] StructuralLabels = ["year", "financial year", "fy", "particulars", "period", "period ended", "as at"];

    /// <summary>A sub-header row that repeats inside a stacked section (e.g. a "Year 2017 2016 2015" row
    /// in the RATIOS block) — its values are the column years, not financial figures.</summary>
    private static bool IsStructuralRow(string label, IReadOnlyList<object?> row, int columnOffset)
    {
        var l = label.Trim().ToLowerInvariant();
        if (StructuralLabels.Contains(l)) return true;

        var vals = new List<int>();
        for (var c = columnOffset; c < row.Count; c++)
        {
            var raw = row[c]?.ToString()?.Trim();
            if (string.IsNullOrEmpty(raw) || raw == "-") continue;
            if (int.TryParse(raw, out var y) && y is >= 1990 and <= 2100) vals.Add(y);
            else return false; // a real value in the row → it's a data row, not a year header
        }
        return vals.Count >= 2; // every value is a 4-digit year → a year header
    }

    private static string? Label(IReadOnlyList<object?> row) => row.Count > 0 ? row[0]?.ToString()?.Trim() : null;

    private static int? FindRowIndex(SheetData sheet, Func<IReadOnlyList<object?>, bool> predicate, int startAt = 0)
    {
        for (var r = startAt; r < sheet.Rows.Count; r++)
            if (predicate(sheet.Rows[r]))
                return r;
        return null;
    }

    private static readonly Regex YearPattern = new(@"(\d{4})", RegexOptions.Compiled);

    private static int? ExtractYear(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = YearPattern.Match(text);
        return match.Success ? int.Parse(match.Value) : null;
    }
}
