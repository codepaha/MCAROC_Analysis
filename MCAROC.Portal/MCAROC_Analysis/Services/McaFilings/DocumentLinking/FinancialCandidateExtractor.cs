using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using UglyToad.PdfPig;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public class ExtractedFinancialCandidate
{
    public bool IsFinancial { get; init; }
    public string? FormType { get; init; }
    public ClassificationConfidence Confidence { get; init; }
    public string Method { get; init; } = string.Empty;

    public int? FinancialYear { get; init; }
    public FinancialBasis Basis { get; init; } = FinancialBasis.Standalone;
    public bool IsXfaPlaceholder { get; init; }

    public int? StatementPageNumber { get; init; }
    public string? StatementTextQuote { get; init; }
    public string? CorroboratedField { get; init; }
    public decimal? CorroboratedAmount { get; init; }
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
                FormType = initialClassification.FormType,
                Confidence = initialClassification.Confidence,
                Method = initialClassification.Method
            };
        }

        int? extractedFy = null;
        var basis = FinancialBasis.Standalone;
        if (fileName.Contains("Consolidated", StringComparison.OrdinalIgnoreCase))
        {
            basis = FinancialBasis.Consolidated;
        }

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
                FormType = initialClassification.FormType,
                Confidence = initialClassification.Confidence,
                Method = initialClassification.Method,
                FinancialYear = extractedFy,
                Basis = basis,
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
                    FinancialYear = extractedFy,
                    Basis = basis
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
                    FormType = classification.FormType,
                    Confidence = classification.Confidence,
                    Method = classification.Method
                };
            }

            var isXfa = page1Text.Contains("Please wait...", StringComparison.OrdinalIgnoreCase);

            if (page1Text.Contains("Consolidated", StringComparison.OrdinalIgnoreCase))
            {
                basis = FinancialBasis.Consolidated;
            }

            var periodMatch = PeriodPattern.Match(page1Text);
            if (periodMatch.Success)
            {
                extractedFy = int.Parse(periodMatch.Groups[2].Value);
            }

            int? statementPage = null;
            string? statementQuote = null;
            string? corroboratedField = null;
            decimal? corroboratedAmount = null;

            if (!isXfa && pageCount > 1)
            {
                // Multi-page native statements: locate statement pages
                for (var p = 1; p <= Math.Min(50, pageCount); p++)
                {
                    var page = doc.GetPage(p);
                    var text = page.Text ?? string.Empty;

                    // Look for Share Capital
                    var scIdx = text.IndexOf("Equity share capital", StringComparison.OrdinalIgnoreCase);
                    if (scIdx < 0) scIdx = text.IndexOf("Share capital", StringComparison.OrdinalIgnoreCase);

                    if (scIdx >= 0)
                    {
                        statementPage = p;
                        var len = Math.Min(80, text.Length - scIdx);
                        var snippet = text.Substring(scIdx, len).Trim();
                        var nl = snippet.IndexOfAny(['\r', '\n']);
                        if (nl > 0) snippet = snippet.Substring(0, nl).Trim();
                        statementQuote = snippet;
                        corroboratedField = nameof(FinancialYearData.ShareCapital);
                        break;
                    }

                    // Fallback to revenue if share capital not yet found
                    var revIdx = text.IndexOf("revenue from operations", StringComparison.OrdinalIgnoreCase);
                    if (revIdx >= 0)
                    {
                        statementPage = p;
                        var len = Math.Min(80, text.Length - revIdx);
                        var snippet = text.Substring(revIdx, len).Trim();
                        var nl = snippet.IndexOfAny(['\r', '\n']);
                        if (nl > 0) snippet = snippet.Substring(0, nl).Trim();
                        statementQuote = snippet;
                        corroboratedField = nameof(FinancialYearData.Revenue);
                        break;
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
                IsXfaPlaceholder = isXfa,
                StatementPageNumber = statementPage,
                StatementTextQuote = statementQuote,
                CorroboratedField = corroboratedField,
                CorroboratedAmount = corroboratedAmount
            };
        }
        catch
        {
            return new ExtractedFinancialCandidate
            {
                IsFinancial = initialClassification.Category == FilingCategory.Financial,
                FinancialYear = extractedFy,
                Basis = basis
            };
        }
    }
}
