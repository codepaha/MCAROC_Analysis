using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings.DocumentLinking;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>Parses "Standalone Financial Data". The sheet stacks three sections (Balance Sheet, P&amp;L, Cash
/// Flow) in one grid. Balance Sheet and P&amp;L share the same year-column header (confirmed by direct
/// inspection). Cash Flow does NOT — the sample data shows it reporting only the most recent N years,
/// left-packed into columns 2..(2+N-1) rather than aligned to the full year list, which we detected by
/// comparing its values against the P&amp;L section's matching row. We map its columns to the last N years
/// of the master year list and log a warning noting that assumption, rather than silently guessing.
/// <para>An LLP's "Financial Data" (#539) has the same grid: STATEMENT OF ASSETS AND LIABILITIES in place of the balance sheet and STATEMENT OF
/// INCOME AND EXPENDITURE in place of the P&amp;L, whose lines are the company's. Its balance sheet is the partners': Contribution Received
/// (→ <see cref="FinancialYearData.ShareCapital"/>) plus Reserves and Surplus is the net worth, Secured plus Unsecured Loans the long-term
/// borrowings. Its tail block is CERTIFIER(s), the per-year certifier table (like a company's AUDITOR(s)), never figures.</para></summary>
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
        // An LLP's statement of assets and liabilities (#539).
        ["Contribution Received"] = nameof(FinancialYearData.ShareCapital),
        ["Short Term Borrowing"] = nameof(FinancialYearData.ShortTermBorrowings),
        ["Creditor / Trade Payables - Advance from Customers"] = nameof(FinancialYearData.TradePayables),
        ["Debtors / Trade Receivables"] = nameof(FinancialYearData.TradeReceivables),
        ["Cash and Cash Equivalents"] = nameof(FinancialYearData.CashAndBank),
    };

    /// <summary>The block that ends the year-column data: a company's AUDITOR(s) or an LLP's CERTIFIER(s), both per-year identity tables.</summary>
    private static bool IsIdentityBlock(string label) =>
        label.StartsWith("AUDITOR", StringComparison.OrdinalIgnoreCase) || label.StartsWith("CERTIFIER", StringComparison.OrdinalIgnoreCase);

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
    /// <summary>Total debt = long-term + short-term borrowings; null only when neither is reported.</summary>
    internal static decimal? TotalDebtOf(decimal? longTerm, decimal? shortTerm) =>
        longTerm is null && shortTerm is null ? null : (longTerm ?? 0m) + (shortTerm ?? 0m);

    public static ParseResult<FinancialYearData> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        out List<FinancialFact> facts, FinancialBasis basis = FinancialBasis.Standalone)
        => Parse(sheet, requestId, ingestionRunId, sourceDocumentId, out facts, out _, basis);

    public static ParseResult<FinancialYearData> Parse(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId,
        out List<FinancialFact> facts, out FinancialLinkTargetCatalog targetCatalog, FinancialBasis basis = FinancialBasis.Standalone)
    {
        var result = new ParseResult<FinancialYearData>();
        facts = [];
        targetCatalog = new FinancialLinkTargetCatalog();
        var targetCatalogSink = targetCatalog;
        var factSink = facts;

        var headerRowIndex = FindRowIndex(sheet, r => Label(r) is { } l
            && (l.StartsWith("BALANCE SHEET", StringComparison.OrdinalIgnoreCase) || l.StartsWith("STATEMENT OF ASSETS AND LIABILITIES", StringComparison.OrdinalIgnoreCase)));
        if (headerRowIndex is null)
        {
            result.AddError(new ParseIssue(IssueSeverity.Error, ParserName, null, null,
                "MISSING_HEADER", $"Could not find the 'BALANCE SHEET' (or an LLP's 'STATEMENT OF ASSETS AND LIABILITIES') header row in {sheet.Name}"));
            return result;
        }

        var headerRow = sheet.Rows[headerRowIndex.Value];
        var years = new List<int?>();
        for (var c = 2; c < headerRow.Count; c++)
            years.Add(ExtractYear(headerRow[c]?.ToString()));

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
                var colHeader = col < headerRow.Count ? headerRow[col]?.ToString()?.Trim() ?? string.Empty : string.Empty;
                var fact = new FinancialFact
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
                };
                captured.Add(fact);

                if (factYears[i].HasValue)
                {
                    targetCatalogSink.Add(new FinancialLinkTarget
                    {
                        TargetKind = FinancialTargetKind.FinancialFact,
                        TargetEntity = fact,
                        FinancialYear = factYears[i]!.Value,
                        Basis = basis,
                        TargetField = label,
                        NumericValue = value,
                        RawValue = raw,
                        Coordinates = new TargetSourceCoordinates(
                            SheetName: sheet.Name,
                            SourceRowNumber: rowNumber + 1,
                            SourceColumnNumber: col + 1,
                            SourceColumnHeader: colHeader,
                            RowLabel: label,
                            TargetField: label,
                            FinancialYear: factYears[i]),
                        YearInferred = yearInferred
                    });
                }
            }
            if (captured.Count > 0) factSink.AddRange(captured); // skip header/sub-total-only rows
        }

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

            if (label.StartsWith("RATIOS", StringComparison.OrdinalIgnoreCase)
                || IsIdentityBlock(label))
                break; // handled by their own dedicated loops below — not more year-column data for
                       // this loop's mapping (the AUDITOR(s) block in particular is a differently-shaped
                       // per-auditor table, not year-per-column; applying this loop's year mapping to it
                       // scrambled auditor firm/PAN/address text into bogus per-year "financial facts")

            if (label.Contains("PROFIT & LOSS", StringComparison.OrdinalIgnoreCase)
                || label.Contains("PROFIT AND LOSS", StringComparison.OrdinalIgnoreCase)
                || label.Contains("STATEMENT OF PROFIT", StringComparison.OrdinalIgnoreCase)
                || label.Contains("STATEMENT OF INCOME AND EXPENDITURE", StringComparison.OrdinalIgnoreCase))
            {
                section = FinancialStatementSection.ProfitAndLoss; // section sub-header, not a line item
                continue;
            }

            if (BalanceSheetAndPnlRowToProperty.TryGetValue(label, out var property))
                ApplyRow(sheet.Rows[r], years, byYear, property, label, GetOrCreate, result, r, targetCatalogSink, sheet.Name, headerRow, basis, yearInferred: false);
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
                    if (label.StartsWith("RATIOS", StringComparison.OrdinalIgnoreCase) || IsIdentityBlock(label))
                        break;

                    if (CashFlowRowToProperty.TryGetValue(label, out var property))
                        ApplyRow(sheet.Rows[r], cashFlowYears, byYear, property, label, GetOrCreate, result, r, targetCatalogSink, sheet.Name, headerRow, basis, yearInferred: cashFlowInferred, columnOffset: 2);
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
                if (IsIdentityBlock(label)) break;
                CaptureFacts(sheet.Rows[r], label, years, FinancialStatementSection.Ratios, r, columnOffset: 2, yearInferred: false);
            }
        }

        // An LLP's statement has no "Total Equity" or long-term borrowings line: its partners' funds are the contribution received plus
        // reserves and surplus, and its long-term borrowings the secured plus unsecured loans (#539). Only for an LLP's statement: a company's
        // figures are never derived this way.
        if (Label(headerRow)?.StartsWith("STATEMENT OF ASSETS AND LIABILITIES", StringComparison.OrdinalIgnoreCase) == true)
        {
            decimal? Fact(int year, string label) =>
                factSink.FirstOrDefault(f => f.FinancialYear == year && f.Section == FinancialStatementSection.BalanceSheet && f.Label == label)?.NumericValue;
            foreach (var e in byYear.Values)
            {
                if (e.NetWorth is null && e.ShareCapital is { } contribution)
                    e.NetWorth = contribution + (Fact(e.FinancialYear, "Reserves and Surplus") ?? 0m);
                var (secured, unsecured) = (Fact(e.FinancialYear, "Secured Loans"), Fact(e.FinancialYear, "Unsecured Loans"));
                if (e.LongTermBorrowings is null && (secured is not null || unsecured is not null))
                    e.LongTermBorrowings = (secured ?? 0m) + (unsecured ?? 0m);
            }
        }

        // The sheet lists long- and short-term borrowings but no total line, so Total Debt was never filled: every dossier showed "—" and the
        // High Leverage rule (debt / net worth) could never run (TruBoard test, 2026-10-13). It is the sum of the two, when either is reported.
        foreach (var e in byYear.Values)
            e.TotalDebt ??= TotalDebtOf(e.LongTermBorrowings, e.ShortTermBorrowings);

        result.Items.AddRange(byYear.Values.OrderBy(f => f.FinancialYear));
        return result;
    }

    /// <summary>One row of the AUDITOR(s) block that sits at the tail of this sheet — a per-year
    /// auditor-identity table (Year, Auditor Name, Membership Number, Firm Name, Firm Registration
    /// Number, PAN, Address), structurally unrelated to the year-per-column financial data above it.
    /// Not merged into <see cref="FinancialFact"/> — the ingestion orchestrator instead merges this into
    /// <see cref="AuditorObservation"/> rows, since identity (who signed off) belongs there, not in the
    /// financial-figures catch-all.</summary>
    public sealed record AuditorIdentityRow(
        int Year, int SourceRowNumber, string? AuditorName, string? MembershipNumber,
        string? FirmName, string? FirmRegistrationNumber);

    public static List<AuditorIdentityRow> ParseAuditorIdentities(SheetData sheet)
    {
        var results = new List<AuditorIdentityRow>();
        var auditorHeaderIndex = FindRowIndex(sheet, r => Label(r)?.StartsWith("AUDITOR", StringComparison.OrdinalIgnoreCase) == true);
        if (auditorHeaderIndex is null) return results;

        for (var r = auditorHeaderIndex.Value + 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var yearText = row.Count > 0 ? row[0]?.ToString()?.Trim() : null;
            if (!int.TryParse(yearText, out var year) || year is < 1990 or > 2100) continue; // column header or blank row

            string? Col(int i) => row.Count > i ? row[i]?.ToString()?.Trim() : null;
            var name = Col(1);
            if (string.IsNullOrWhiteSpace(name)) continue;

            results.Add(new AuditorIdentityRow(year, r + 1, name, Col(2), Col(3), Col(4)));
        }
        return results;
    }

    private static void ApplyRow(
        IReadOnlyList<object?> row,
        IReadOnlyList<int?> years,
        Dictionary<int, FinancialYearData> byYear,
        string property,
        string label,
        Func<int, FinancialYearData> getOrCreate,
        ParseResult<FinancialYearData> result,
        int rowNumber,
        FinancialLinkTargetCatalog targetCatalog,
        string sheetName,
        IReadOnlyList<object?> headerRow,
        FinancialBasis basis,
        bool yearInferred,
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

            var colHeader = col < headerRow.Count ? headerRow[col]?.ToString()?.Trim() ?? string.Empty : string.Empty;
            targetCatalog.Add(new FinancialLinkTarget
            {
                TargetKind = FinancialTargetKind.FinancialYearData,
                TargetEntity = entity,
                FinancialYear = year.Value,
                Basis = basis,
                TargetField = property,
                NumericValue = value,
                RawValue = raw,
                Coordinates = new TargetSourceCoordinates(
                    SheetName: sheetName,
                    SourceRowNumber: rowNumber + 1,
                    SourceColumnNumber: col + 1,
                    SourceColumnHeader: colHeader,
                    RowLabel: label,
                    TargetField: property,
                    FinancialYear: year.Value),
                YearInferred = yearInferred
            });
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
