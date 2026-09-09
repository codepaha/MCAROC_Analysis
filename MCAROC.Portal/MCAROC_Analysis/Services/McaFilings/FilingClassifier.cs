using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings;

public record ClassificationResult(FilingCategory Category, string? FormType, ClassificationConfidence Confidence, string Method, string? MatchedRule);

/// <summary>Classifies one filing document into a category and best-effort form type. Pure function, no
/// I/O — evaluated as a hierarchy, stopping at the first confident match:
///   1. Strong filename keyword (High confidence)
///   2. First-page text keywords, once available (Medium confidence) — exists specifically for generic
///      filenames like "Optional Attachment-(1)" (170 occurrences, the single most common name in the
///      real corpus) that carry no signal on their own.
///   3. Outer category folder fallback (Low confidence) — NOT a blind default: "Charge Documents
///      Financial Documets" [sic] contains both charge and financial documents by its own name, so this
///      only resolves Charge when nothing stronger matched.
///   4. Unclassified.
/// Seeded from the real label distribution tallied against the sample corpus.</summary>
public static class FilingClassifier
{
    // Order matters: financial/XBRL forms (Form 23AC, 23ACA, AOC-4) must be checked before the generic
    // "Form 23" compliance rule, since "Form 23AC XBRL" would otherwise match "form 23" first.
    private static readonly (string Keyword, FilingCategory Category, string FormType)[] FilenameRules =
    [
        // Financial / XBRL
        ("xbrl", FilingCategory.Financial, "XBRL Financial Statement"),
        ("aoc-4", FilingCategory.Financial, "Form AOC-4"),
        ("formschv", FilingCategory.Financial, "Form Sch-V"),
        ("23ac", FilingCategory.Financial, "Form 23AC/23ACA (XBRL)"), // matches both "23ac" and "23aca"

        // Charge
        ("chg-1", FilingCategory.Charge, "Form CHG-1"),
        ("form 8", FilingCategory.Charge, "Form 8"),
        ("form 17", FilingCategory.Charge, "Form 17"),
        ("instrument(s) of creation", FilingCategory.Charge, "Instrument of Charge"),
        ("instrument of creation", FilingCategory.Charge, "Instrument of Charge"),
        ("letter of the charge holder", FilingCategory.Charge, "Charge Satisfaction Letter"),
        ("joint charge holder", FilingCategory.Charge, "Joint Charge Holder Particulars"),

        // Compliance
        ("mgt-14", FilingCategory.Compliance, "Form MGT-14"),
        ("mgt-7", FilingCategory.Compliance, "Form MGT-7"),
        ("mgt-8", FilingCategory.Compliance, "Form MGT-8"),
        ("adt-1", FilingCategory.Compliance, "Form ADT-1"),
        ("adt-3", FilingCategory.Compliance, "Form ADT-3"),
        ("inc-22", FilingCategory.Compliance, "Form INC-22"),
        ("inc-28", FilingCategory.Compliance, "Form INC-28"),
        ("pas-3", FilingCategory.Compliance, "Form PAS-3"),
        ("extension of financial year", FilingCategory.Compliance, "AGM Extension Approval"),
        ("extension of agm", FilingCategory.Compliance, "AGM Extension Approval"),
        ("form 23", FilingCategory.Compliance, "Form 23"), // after the 23ac/23aca financial rule above

        // Constitutional
        ("memorandum of association", FilingCategory.Constitutional, "MoA"),
        ("moa", FilingCategory.Constitutional, "MoA"),
        ("articles of association", FilingCategory.Constitutional, "AoA"),
        ("aoa", FilingCategory.Constitutional, "AoA"),
        ("certificate of incorporation", FilingCategory.Constitutional, "Certificate of Incorporation"),
    ];

    private static readonly (string Phrase, FilingCategory Category, string FormType)[] TextHeaderRules =
    [
        ("creation or modification of charge", FilingCategory.Charge, "Charge Instrument"),
        ("amount has been satisfied", FilingCategory.Charge, "Charge Satisfaction Letter"),
        ("notice of appointment of auditor", FilingCategory.Compliance, "Form ADT-1"),
        ("notice of resignation of auditor", FilingCategory.Compliance, "Form ADT-3"),
        ("notice of situation or change of situation", FilingCategory.Compliance, "Form INC-22"),
        ("return of allotment", FilingCategory.Compliance, "Form PAS-3"),
        ("memorandum of association", FilingCategory.Constitutional, "MoA"),
        ("articles of association", FilingCategory.Constitutional, "AoA"),
        ("certificate of incorporation", FilingCategory.Constitutional, "Certificate of Incorporation"),
    ];

    public static ClassificationResult Classify(string outerCategoryFolder, string sourceFolder, string originalFileName, string? firstPageText)
    {
        var nameLower = originalFileName.ToLowerInvariant();
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
        if (folderLower.Contains("charge"))
            return new ClassificationResult(FilingCategory.Charge, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);
        if (folderLower.Contains("incorporation"))
            return new ClassificationResult(FilingCategory.Constitutional, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);

        return new ClassificationResult(FilingCategory.Unclassified, null, ClassificationConfidence.Low, "NoMatch", null);
    }
}
