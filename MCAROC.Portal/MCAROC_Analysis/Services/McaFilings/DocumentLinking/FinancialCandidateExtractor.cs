using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class ExtractedFinancialCandidate
{
    public bool IsFinancial { get; init; }
    public FilingCategory Category { get; init; }
    public string? FormType { get; init; }
    public ClassificationConfidence Confidence { get; init; }
    public string Method { get; init; } = string.Empty;

    public int? FinancialYear { get; init; }
    public FinancialBasis? Basis { get; init; }
    public bool HasConflictingBasis { get; init; }
    public bool IsXfaPlaceholder { get; init; }

    public int? StatementPageNumber { get; init; }
    public string? StatementTextQuote { get; init; }
    public string? CorroboratedField { get; init; }
    public decimal? CorroboratedAmount { get; init; }
    public string? CorroboratedUnit { get; init; }
}

public static class FinancialCandidateExtractor
{
    private static readonly Regex PeriodPattern = new(
        @"period\s+\d{2}/\d{2}/(\d{4})\s+to\s+\d{2}/\d{2}/(\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FyFilenamePattern1 = new(
        @"(?:for the (?:fy|financial year) ending on[- ]*)(\d{2})[-/.]?(\d{2})[-/.]?(\d{2,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FyFilenamePattern2 = new(
        @"31[-/.]?03[-/.]?(\d{2,4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RunningHeaderPattern = new(
        @"COASTAL\s+PROJECTS\s+LIMITED\s*(Standalone|Consolidated)\s*(?:Financial\s+Statements|Balance\s+Sheet|Profit\s+and\s+Loss(?:\s+account)?|Statement\s+of\s+Profit\s+and\s+Loss)?\s*for\s+(?:the\s+)?period\s+\d{2}/\d{2}/\d{4}\s+to\s+\d{2}/\d{2}/(\d{4})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NatureOfReportPattern = new(
        @"Nature\s+of\s+report\s+(?:standalone\s+consolidated\s+)?(Standalone|Consolidated)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex StatementBasisPattern = new(
        @"(?:^|\n|\s)(Standalone|Consolidated)\s+(?:Balance\s+Sheet|Financial\s+Statements|Profit\s+and\s+Loss|Statement\s+of\s+Profit\s+and\s+Loss)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MillionsUnitPattern = new(
        @"all\s+monetary\s+values\s+are\s+in\s+millions\s+of\s+inr|millions\s+of\s+inr|\(in\s+millions?\)|rs\.\s*in\s*millions?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LakhsUnitPattern = new(
        @"all\s+monetary\s+values\s+are\s+in\s+lakhs\s+of\s+inr|lakhs\s+of\s+inr|\(in\s+lakhs?\)|rs\.\s*in\s*lakhs?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CroresUnitPattern = new(
        @"all\s+monetary\s+values\s+are\s+in\s+crores\s+of\s+inr|crores\s+of\s+inr|\(in\s+crores?\)|rs\.\s*in\s*crores?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ShareCapitalPattern = new(
        @"(?:Equity\s+share\s+capital|Share\s+capital)\s*[:\-]?\s*(\d{1,3}(?:,\d{2,3})*(?:\.\d{1,2})|\d{1,3}(?:,\d{2})*,\d{3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RevenuePattern = new(
        @"(?:Revenue\s+from\s+operations|Net\s+Revenue|Income\s+from\s+operations)\s*(?:\[Abstract\])?\s*[:\-]?\s*(\d{1,3}(?:,\d{2,3})*(?:\.\d{1,2})|\d{1,3}(?:,\d{2})*,\d{3})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static ExtractedFinancialCandidate Extract(
        string outerCategoryFolder,
        string sourceFolder,
        string fileName,
        Stream? pdfStream)
    {
        // 1. Initial classification without text
        var initialClassification = CoastalFinancialClassifier.Classify(outerCategoryFolder, sourceFolder, fileName, firstPageText: null);

        // If no stream provided and not classified as financial, exit early
        if (pdfStream is null && initialClassification.Category != FilingCategory.Financial)
        {
            return new ExtractedFinancialCandidate
            {
                IsFinancial = false,
                Category = initialClassification.Category,
                FormType = initialClassification.FormType,
                Confidence = initialClassification.Confidence,
                Method = initialClassification.Method
            };
        }

        int? extractedFy = null;
        var filenameConsolidated = fileName.Contains("Consolidated", StringComparison.OrdinalIgnoreCase);
        var filenameStandalone = fileName.Contains("Standalone", StringComparison.OrdinalIgnoreCase);

        // Try extracting FY from filename
        var m1 = FyFilenamePattern1.Match(fileName);
        if (m1.Success)
        {
            var yrVal = int.Parse(m1.Groups[1].Value.Length == 4 ? m1.Groups[1].Value : (m1.Groups[3].Value.Length > 0 ? m1.Groups[3].Value : m1.Groups[1].Value));
            extractedFy = yrVal < 100 ? 2000 + yrVal : yrVal;
        }
        else
        {
            var m2 = FyFilenamePattern2.Match(fileName);
            if (m2.Success)
            {
                var yrVal = int.Parse(m2.Groups[1].Value);
                extractedFy = yrVal < 100 ? 2000 + yrVal : yrVal;
            }
        }

        if (pdfStream is null)
        {
            return new ExtractedFinancialCandidate
            {
                IsFinancial = initialClassification.Category == FilingCategory.Financial,
                Category = initialClassification.Category,
                FormType = initialClassification.FormType,
                Confidence = initialClassification.Confidence,
                Method = initialClassification.Method,
                FinancialYear = extractedFy,
                Basis = filenameConsolidated ? FinancialBasis.Consolidated : (filenameStandalone ? FinancialBasis.Standalone : null),
                IsXfaPlaceholder = false
            };
        }

        // 2. Read PDF text via PdfPig
        try
        {
            using var doc = PdfDocument.Open(pdfStream);
            var pageCount = doc.NumberOfPages;
            if (pageCount == 0)
            {
                return new ExtractedFinancialCandidate
                {
                    IsFinancial = initialClassification.Category == FilingCategory.Financial,
                    Category = initialClassification.Category,
                    FormType = initialClassification.FormType,
                    Confidence = initialClassification.Confidence,
                    Method = initialClassification.Method,
                    FinancialYear = extractedFy,
                    Basis = filenameConsolidated ? FinancialBasis.Consolidated : (filenameStandalone ? FinancialBasis.Standalone : null)
                };
            }

            var page1 = doc.GetPage(1);
            var page1Text = page1.Text ?? string.Empty;

            var classification = CoastalFinancialClassifier.Classify(outerCategoryFolder, sourceFolder, fileName, page1Text);
            if (classification.Category != FilingCategory.Financial)
            {
                return new ExtractedFinancialCandidate
                {
                    IsFinancial = false,
                    Category = classification.Category,
                    FormType = classification.FormType,
                    Confidence = classification.Confidence,
                    Method = classification.Method
                };
            }

            var isXfa = page1Text.Contains("Please wait...", StringComparison.OrdinalIgnoreCase);

            var periodMatch = PeriodPattern.Match(page1Text);
            if (periodMatch.Success)
            {
                extractedFy = int.Parse(periodMatch.Groups[2].Value);
            }

            // Check running headers across first few pages for explicit basis and period
            FinancialBasis? docDeclaredBasis = null;
            for (var p = 1; p <= Math.Min(5, pageCount); p++)
            {
                var text = doc.GetPage(p).Text ?? string.Empty;
                var hm = RunningHeaderPattern.Match(text);
                if (hm.Success)
                {
                    docDeclaredBasis = hm.Groups[1].Value.Equals("Consolidated", StringComparison.OrdinalIgnoreCase)
                        ? FinancialBasis.Consolidated
                        : FinancialBasis.Standalone;
                    extractedFy = int.Parse(hm.Groups[2].Value);
                    break;
                }

                var nm = NatureOfReportPattern.Match(text);
                if (nm.Success && !docDeclaredBasis.HasValue)
                {
                    docDeclaredBasis = nm.Groups[1].Value.Equals("Consolidated", StringComparison.OrdinalIgnoreCase)
                        ? FinancialBasis.Consolidated
                        : FinancialBasis.Standalone;
                }

                var sm = StatementBasisPattern.Match(text);
                if (sm.Success && !docDeclaredBasis.HasValue)
                {
                    docDeclaredBasis = sm.Groups[1].Value.Equals("Consolidated", StringComparison.OrdinalIgnoreCase)
                        ? FinancialBasis.Consolidated
                        : FinancialBasis.Standalone;
                }
            }

            // Resolve basis & conflict
            bool hasConflictingBasis = false;
            FinancialBasis? basis = null;

            if (docDeclaredBasis.HasValue)
            {
                if (docDeclaredBasis == FinancialBasis.Standalone && filenameConsolidated)
                {
                    hasConflictingBasis = true;
                    basis = FinancialBasis.Standalone;
                }
                else if (docDeclaredBasis == FinancialBasis.Consolidated && filenameStandalone)
                {
                    hasConflictingBasis = true;
                    basis = FinancialBasis.Consolidated;
                }
                else
                {
                    basis = docDeclaredBasis.Value;
                }
            }
            else
            {
                if (filenameConsolidated && !filenameStandalone)
                {
                    basis = FinancialBasis.Consolidated;
                }
                else if (filenameStandalone && !filenameConsolidated)
                {
                    basis = FinancialBasis.Standalone;
                }
                else if (filenameStandalone && filenameConsolidated)
                {
                    hasConflictingBasis = true;
                    basis = null;
                }
                else
                {
                    // Neither filename nor PDF header identifies the basis: preserve null
                    basis = null;
                }
            }

            int? statementPage = null;
            string? statementQuote = null;
            string? corroboratedField = null;
            decimal? corroboratedAmount = null;
            string? corroboratedUnit = null;

            if (!isXfa && pageCount > 1)
            {
                // Multi-page native statements: locate statement pages
                // 1. Search for Share Capital in Balance Sheet pages
                for (var p = 1; p <= Math.Min(80, pageCount); p++)
                {
                    var page = doc.GetPage(p);
                    var text = page.Text ?? string.Empty;

                    if (text.Contains("Balance sheet", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("[100100]", StringComparison.OrdinalIgnoreCase) ||
                        text.Contains("[110000]", StringComparison.OrdinalIgnoreCase))
                    {
                        var scMatch = ShareCapitalPattern.Match(text);
                        if (scMatch.Success)
                        {
                            var cleanStr = scMatch.Groups[1].Value.Replace(",", "").Trim();
                            if (decimal.TryParse(cleanStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var rawAmount))
                            {
                                decimal unitMultiplier = 0.0000001m; // default INR
                                string unitName = "INR";

                                if (MillionsUnitPattern.IsMatch(text))
                                {
                                    unitMultiplier = 0.1m;
                                    unitName = "Millions";
                                }
                                else if (LakhsUnitPattern.IsMatch(text))
                                {
                                    unitMultiplier = 0.01m;
                                    unitName = "Lakhs";
                                }
                                else if (CroresUnitPattern.IsMatch(text))
                                {
                                    unitMultiplier = 1.0m;
                                    unitName = "Crores";
                                }

                                statementPage = p;
                                var len = Math.Min(80, text.Length - scMatch.Index);
                                var snippet = text.Substring(scMatch.Index, len).Trim();
                                var nl = snippet.IndexOfAny(['\r', '\n']);
                                if (nl > 0) snippet = snippet.Substring(0, nl).Trim();
                                statementQuote = snippet;
                                corroboratedField = nameof(FinancialYearData.ShareCapital);
                                corroboratedAmount = Math.Round(rawAmount * unitMultiplier, 2);
                                corroboratedUnit = unitName;
                                break;
                            }
                        }
                    }
                }

                // 2. If Share Capital not found, search for Revenue in Profit & Loss pages
                if (!statementPage.HasValue)
                {
                    for (var p = 1; p <= Math.Min(80, pageCount); p++)
                    {
                        var page = doc.GetPage(p);
                        var text = page.Text ?? string.Empty;

                        if (text.Contains("profit and loss", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("[120000]", StringComparison.OrdinalIgnoreCase))
                        {
                            var revMatch = RevenuePattern.Match(text);
                            if (revMatch.Success)
                            {
                                var cleanStr = revMatch.Groups[1].Value.Replace(",", "").Trim();
                                if (decimal.TryParse(cleanStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var rawAmount))
                                {
                                    decimal unitMultiplier = 0.0000001m;
                                    string unitName = "INR";

                                    if (MillionsUnitPattern.IsMatch(text))
                                    {
                                        unitMultiplier = 0.1m;
                                        unitName = "Millions";
                                    }
                                    else if (LakhsUnitPattern.IsMatch(text))
                                    {
                                        unitMultiplier = 0.01m;
                                        unitName = "Lakhs";
                                    }
                                    else if (CroresUnitPattern.IsMatch(text))
                                    {
                                        unitMultiplier = 1.0m;
                                        unitName = "Crores";
                                    }

                                    statementPage = p;
                                    var len = Math.Min(80, text.Length - revMatch.Index);
                                    var snippet = text.Substring(revMatch.Index, len).Trim();
                                    var nl = snippet.IndexOfAny(['\r', '\n']);
                                    if (nl > 0) snippet = snippet.Substring(0, nl).Trim();
                                    statementQuote = snippet;
                                    corroboratedField = nameof(FinancialYearData.Revenue);
                                    corroboratedAmount = Math.Round(rawAmount * unitMultiplier, 2);
                                    corroboratedUnit = unitName;
                                    break;
                                }
                            }
                        }
                    }
                }
            }

            return new ExtractedFinancialCandidate
            {
                IsFinancial = true,
                FormType = classification.FormType,
                Confidence = classification.Confidence,
                Method = classification.Method,
                FinancialYear = extractedFy,
                Basis = basis,
                HasConflictingBasis = hasConflictingBasis,
                IsXfaPlaceholder = isXfa,
                StatementPageNumber = statementPage,
                StatementTextQuote = statementQuote,
                CorroboratedField = corroboratedField,
                CorroboratedAmount = corroboratedAmount,
                CorroboratedUnit = corroboratedUnit
            };
        }
        catch
        {
            return new ExtractedFinancialCandidate
            {
                IsFinancial = initialClassification.Category == FilingCategory.Financial,
                Category = initialClassification.Category,
                FormType = initialClassification.FormType,
                Confidence = initialClassification.Confidence,
                Method = initialClassification.Method,
                FinancialYear = extractedFy,
                Basis = filenameConsolidated ? FinancialBasis.Consolidated : (filenameStandalone ? FinancialBasis.Standalone : null)
            };
        }
    }
}
