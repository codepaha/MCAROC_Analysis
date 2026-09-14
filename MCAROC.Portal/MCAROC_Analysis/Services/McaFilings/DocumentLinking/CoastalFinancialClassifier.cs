using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.McaFilings;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

/// <summary>
/// Pilot-specific classifier for Deliverable 3 financial filing recognition.
/// Keeps the shared production FilingClassifier completely untouched to safeguard live batch ingestion,
/// while providing dedicated first-page text and folder rules for the pilot pipeline.
/// </summary>
public static class CoastalFinancialClassifier
{
    private static readonly (string Keyword, FilingCategory Category, string FormType)[] FilenameRules =
    [
        ("aoc-4", FilingCategory.Financial, "Form AOC-4"),
        ("formschv", FilingCategory.Financial, "Form Sch-V"),
        ("23ac", FilingCategory.Financial, "Form 23AC/23ACA (XBRL)"),
        ("xbrl", FilingCategory.Financial, "XBRL Financial Statement"),

        // Compliance & other rules to distinguish non-financial files in pilot
        ("extension of financial year", FilingCategory.Compliance, "AGM Extension Approval"),
        ("extension of agm", FilingCategory.Compliance, "AGM Extension Approval"),
        ("chg-1", FilingCategory.Charge, "Form CHG-1"),
        ("form 8", FilingCategory.Charge, "Form 8"),
        ("form 17", FilingCategory.Charge, "Form 17"),
    ];

    private static readonly (string Phrase, FilingCategory Category, string FormType)[] TextHeaderRules =
    [
        ("balance sheet", FilingCategory.Financial, "Balance Sheet"),
        ("statement of profit and loss", FilingCategory.Financial, "Profit and Loss Statement"),
        ("profit and loss account", FilingCategory.Financial, "Profit and Loss Statement"),
        ("financial statements", FilingCategory.Financial, "Financial Statement"),
        ("cash flow statement", FilingCategory.Financial, "Cash Flow Statement"),
        ("independent auditor's report", FilingCategory.Financial, "Auditor's Report"),
        ("creation or modification of charge", FilingCategory.Charge, "Charge Instrument"),
        ("amount has been satisfied", FilingCategory.Charge, "Charge Satisfaction Letter"),
    ];

    public static ClassificationResult Classify(string outerCategoryFolder, string sourceFolder, string originalFileName, string? firstPageText)
    {
        var nameLower = originalFileName.ToLowerInvariant();

        // Check compliance extension first so "Approval letter of extension of financial year" doesn't falsely match financial
        if (nameLower.Contains("extension of financial year") || nameLower.Contains("extension of agm"))
        {
            return new ClassificationResult(FilingCategory.Compliance, "AGM Extension Approval", ClassificationConfidence.High, "FilenameKeyword", "extension of financial year");
        }

        foreach (var rule in FilenameRules)
        {
            if (nameLower.Contains(rule.Keyword))
                return new ClassificationResult(rule.Category, rule.FormType, ClassificationConfidence.High, "FilenameKeyword", rule.Keyword);
        }

        if (!string.IsNullOrWhiteSpace(firstPageText))
        {
            var textLower = firstPageText.ToLowerInvariant();
            foreach (var rule in TextHeaderRules)
            {
                if (textLower.Contains(rule.Phrase))
                    return new ClassificationResult(rule.Category, rule.FormType, ClassificationConfidence.Medium, "TextHeader", rule.Phrase);
            }
        }

        var folderLower = outerCategoryFolder.ToLowerInvariant();
        // Pure financial folder (without charge) falls back to Financial
        if (folderLower.Contains("financial") && !folderLower.Contains("charge"))
            return new ClassificationResult(FilingCategory.Financial, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);

        // Mixed folder "Charge Documents Financial Documets" or any charge folder falls back to Charge (zero false positives)
        if (folderLower.Contains("charge"))
            return new ClassificationResult(FilingCategory.Charge, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);

        if (folderLower.Contains("incorporation"))
            return new ClassificationResult(FilingCategory.Constitutional, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);

        return new ClassificationResult(FilingCategory.Unclassified, null, ClassificationConfidence.Low, "NoMatch", null);
    }
}
