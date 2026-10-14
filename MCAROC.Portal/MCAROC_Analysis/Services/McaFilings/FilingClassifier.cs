using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings;

public record ClassificationResult(FilingCategory Category, string? FormType, ClassificationConfidence Confidence, string Method, string? MatchedRule);

/// <summary>Classifies one filing document into a category and best-effort form type. Pure function, no
/// I/O — evaluated as a hierarchy, stopping at the first confident match:
///   1. Filename rule (High confidence), on the name with its MCA hash prefix, numbering and separators normalised, so "AOC-4", "AOC 4" and
///      "aoc4" read alike and short codes match as whole words only ("moa" is not inside "Namoa").
///   2. Title text (Medium confidence) — the opening of the document only, never its body: a resolution that "alters the memorandum of
///      association" is a resolution, not an MoA. Exists for generic filenames like "Optional Attachment-(1)", the commonest name in the corpus.
///   3. Outer category folder fallback (Low confidence) — NOT a blind default: "Charge Documents
///      Financial Documets" [sic] contains both charge and financial documents by its own name, so this
///      only resolves Charge when nothing stronger matched.
///   4. Unclassified.
/// Seeded from the real label distribution of the corpus; the rules added for #499 come from the 823 documents that were unclassified or placed
/// only by their folder across six companies (dev DB, 2026-10-14).</summary>
public static partial class FilingClassifier
{
    /// <summary>Bumped whenever the rules change, so stored documents are classified again (<see cref="FilingReclassifier"/>).</summary>
    public const int Version = 2;

    /// <summary>How much of the extracted text counts as the title region.</summary>
    internal const int TitleRegionChars = 2500;
    /// <summary>MoA / AoA must be named at the very top: a long document mentioning them further down is something else.</summary>
    internal const int CharterTitleChars = 600;

    private sealed record Rule(string Name, Regex Pattern, FilingCategory Category, string FormType);

    private static Rule R(string name, string pattern, FilingCategory category, string formType) =>
        new(name, new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled), category, formType);

    // Order matters: the first match wins. Specific before generic (XBRL/23AC before "form 23"; the satisfaction certificate before the
    // registration certificate; the name-change certificate before the plain certificate of incorporation).
    private static readonly Rule[] FilenameRules =
    [
        // Financial / XBRL
        R("xbrl", @"\bxbrl\b", FilingCategory.Financial, "XBRL Financial Statement"),
        R("aoc-4", @"\baoc ?4", FilingCategory.Financial, "Form AOC-4"),
        R("aoc-2", @"\baoc ?2\b", FilingCategory.Financial, "Form AOC-2"),
        R("formschv", @"form ?sch ?v\b|formschv", FilingCategory.Financial, "Form Sch-V"),
        R("23ac", @"23 ?aca?\b", FilingCategory.Financial, "Form 23AC/23ACA (XBRL)"),   // "form23ac" has no space
        R("financial statements", @"financial sta\w*ments?|\bfinancials\b|balance sheet|audit\w* report|annual report|\bfs\b", FilingCategory.Financial, "Financial Statements"),
        R("directors report", @"director\w*'?s? report|board'?s report", FilingCategory.Financial, "Directors' Report"),

        // Charge
        R("satisfaction certificate", @"certificate of (registration of )?(the )?satisfaction of charge", FilingCategory.Charge, "Certificate of Satisfaction of Charge"),
        R("charge certificate", @"certificate of registration of (the )?(charge|modification)", FilingCategory.Charge, "Certificate of Registration of Charge"),
        R("chg-1", @"\bchg ?1\b", FilingCategory.Charge, "Form CHG-1"),
        R("chg-4", @"\bchg ?4\b", FilingCategory.Charge, "Form CHG-4"),
        R("chg-9", @"\bchg ?9\b", FilingCategory.Charge, "Form CHG-9"),
        R("form chg", @"\bform chg\b", FilingCategory.Charge, "Form CHG"),
        R("form 8", @"\bform 8\b", FilingCategory.Charge, "Form 8"),
        R("form 17", @"\bform 17\b", FilingCategory.Charge, "Form 17"),
        R("instrument of creation", @"instrument\(?s?\)? of creation|deed of hypothecation|hypothecation deed|\bdoh\b", FilingCategory.Charge, "Instrument of Charge"),
        R("letter of the charge holder", @"letter of the charge holder", FilingCategory.Charge, "Charge Satisfaction Letter"),
        R("joint charge holder", @"joint charge holder", FilingCategory.Charge, "Joint Charge Holder Particulars"),

        // Compliance
        R("mgt-14", @"\bmgt ?14\b", FilingCategory.Compliance, "Form MGT-14"),
        R("mgt-7", @"\bmgt ?7a?\b", FilingCategory.Compliance, "Form MGT-7"),
        R("mgt-8", @"\bmgt ?8\b", FilingCategory.Compliance, "Form MGT-8"),
        R("adt-1", @"\badt ?1\b", FilingCategory.Compliance, "Form ADT-1"),
        R("adt-3", @"\badt ?3\b", FilingCategory.Compliance, "Form ADT-3"),
        R("auditor papers", @"auditor'?s? (appointment|resignation|consent)|(appointment|resignation|consent) (letter )?of (the )?auditor|for aud\w*tors? appointment|auditor resignation", FilingCategory.Compliance, "Auditor Appointment / Resignation (ADT attachment)"),
        R("inc-22", @"\binc ?22\b", FilingCategory.Compliance, "Form INC-22"),
        R("inc-28", @"\binc ?28\b", FilingCategory.Compliance, "Form INC-28"),
        R("pas-3", @"\bpas ?3\b", FilingCategory.Compliance, "Form PAS-3"),
        R("pas-5 / allottees", @"\bpas ?5|list ?of ?(allot\w*|allotment)|listofallot", FilingCategory.Compliance, "List of Allottees (PAS-5)"),
        R("sh-7", @"\bsh ?7\b|\bform sh\b", FilingCategory.Compliance, "Form SH-7"),
        R("dpt-3", @"\bdpt ?3\b|\bform dpt\b", FilingCategory.Compliance, "Form DPT-3"),
        R("msme", @"\bmsme\b", FilingCategory.Compliance, "Form MSME-1"),
        R("director attachment", @"declaration of the appointee|evidence of cessation|interest in other entit|notice of resignation|\bconsent (letter|to act)\b|\bappointment letter\b|resignation letter|details of other entit", FilingCategory.Compliance, "Director Appointment / Cessation (DIR-12 attachment)"),
        R("member/director list", @"list of (share ?holders?|shareholders?|members|debenture|directors?|bm\b)", FilingCategory.Compliance, "List of Shareholders / Directors"),
        R("agm extension", @"exten\w*ion of (agm|financial year)|approval letter for exten|\bextension\b.*\bagm\b|roc .*extension", FilingCategory.Compliance, "AGM Extension Approval"),
        R("condonation", @"condonation of delay", FilingCategory.Compliance, "Condonation of Delay Order"),
        R("valuation report", @"valuation report", FilingCategory.Compliance, "Valuation Report"),
        R("csr policy", @"\bcsr\b", FilingCategory.Compliance, "CSR Policy"),
        R("shareholder resolution", @"special resolution|explana\w*ry statement|\begm\b|\bagm (resolution|at shorter notice)|shorter notice", FilingCategory.Compliance, "Shareholder Resolution"),
        R("board resolution", @"board resolution|\bctc\b.*\b(br|resolution|allotment)\b|\bbr\b.*allotment", FilingCategory.Compliance, "Board Resolution"),
        R("form 23", @"\bform 23\b", FilingCategory.Compliance, "Form 23"), // after the 23ac/23aca financial rule above
        // Forms of the Companies Act 1956 (before 2014), still in older companies' filings.
        R("form 66", @"\bform ?66\b", FilingCategory.Compliance, "Form 66 (Compliance Certificate)"),
        R("form 20b", @"\bform ?20 ?b\b", FilingCategory.Compliance, "Form 20B (Annual Return)"),
        R("form 32", @"\bform ?32\b", FilingCategory.Compliance, "Form 32 (Directors)"),
        R("form 18", @"\bform ?18\b", FilingCategory.Compliance, "Form 18 (Registered Office)"),
        R("form 5", @"\bform ?5\b", FilingCategory.Compliance, "Form 5 (Share Capital)"),
        R("form 2", @"\bform ?2\b", FilingCategory.Compliance, "Form 2 (Return of Allotment)"),

        // Constitutional
        R("name change certificate", @"certificate of incorporation (pursuant|consequent) (to|upon) change of name|fresh certificate of incorporation|change of name", FilingCategory.Constitutional, "Certificate of Incorporation on Change of Name"),
        // "moa"/"aoa" as a whole word, or run together with a charter word ("aoasubcribersheet", misspelt as filed); never the start of another word ("Moana", "Aoaki").
        R("memorandum of association", @"memorandum of association|\bmoa(?=\b|sub|alter|amend)", FilingCategory.Constitutional, "MoA"),
        R("articles of association", @"articles of association|\baoa(?=\b|sub|alter|amend)", FilingCategory.Constitutional, "AoA"),
        R("certificate of incorporation", @"certificate of incorporation", FilingCategory.Constitutional, "Certificate of Incorporation"),
    ];

    // Read in the title region only (TitleRegionChars); MoA/AoA only at the very top (CharterTitleChars).
    private static readonly Rule[] TextHeaderRules =
    [
        R("satisfaction certificate", @"certificate of registration of (the )?satisfaction of charge", FilingCategory.Charge, "Certificate of Satisfaction of Charge"),
        R("charge certificate", @"certificate of registration of (the )?(charge|modification of charge)", FilingCategory.Charge, "Certificate of Registration of Charge"),
        R("name change certificate", @"(pursuant|consequent) (to|upon) change of name|fresh certificate of incorporation", FilingCategory.Constitutional, "Certificate of Incorporation on Change of Name"),
        R("deed of hypothecation", @"deed of hypothecation|hypothecation deed", FilingCategory.Charge, "Instrument of Charge"),
        R("creation or modification of charge", @"creation or modification of charge", FilingCategory.Charge, "Charge Instrument"),
        R("amount has been satisfied", @"amount has been satisfied", FilingCategory.Charge, "Charge Satisfaction Letter"),
        R("notice of appointment of auditor", @"notice of appointment of auditor", FilingCategory.Compliance, "Form ADT-1"),
        R("notice of resignation of auditor", @"notice of resignation of auditor", FilingCategory.Compliance, "Form ADT-3"),
        R("notice of situation or change of situation", @"notice of situation or change of situation", FilingCategory.Compliance, "Form INC-22"),
        R("return of allotment", @"return of allotment", FilingCategory.Compliance, "Form PAS-3"),
        R("list of allottees", @"list of allot+e+s", FilingCategory.Compliance, "List of Allottees (PAS-5)"),
        R("special resolution", @"special resolution", FilingCategory.Compliance, "Shareholder Resolution"),
        R("board resolution", @"certified true copy of (the )?resolution|resolution passed (at|by) the (meeting of the )?board", FilingCategory.Compliance, "Board Resolution"),
        R("auditors report", @"independent auditor'?s report|balance sheet as at", FilingCategory.Financial, "Financial Statements"),
        R("directors report", @"directors'? report|board'?s report", FilingCategory.Financial, "Directors' Report"),
        R("certificate of incorporation", @"certificate of incorporation", FilingCategory.Constitutional, "Certificate of Incorporation"),
    ];

    private static readonly Rule[] CharterTitleRules =
    [
        R("memorandum of association", @"memorandum of association", FilingCategory.Constitutional, "MoA"),
        R("articles of association", @"articles of association", FilingCategory.Constitutional, "AoA"),
    ];

    public static ClassificationResult Classify(string outerCategoryFolder, string sourceFolder, string originalFileName, string? firstPageText)
    {
        var name = NormaliseName(originalFileName);
        foreach (var rule in FilenameRules)
            if (rule.Pattern.IsMatch(name))
                return new ClassificationResult(rule.Category, rule.FormType, ClassificationConfidence.High, "FilenameKeyword", rule.Name);

        if (!string.IsNullOrWhiteSpace(firstPageText))
        {
            var title = Whitespace().Replace(firstPageText.Length > TitleRegionChars ? firstPageText[..TitleRegionChars] : firstPageText, " ").ToLowerInvariant();
            var top = title.Length > CharterTitleChars ? title[..CharterTitleChars] : title;
            foreach (var rule in CharterTitleRules)
                if (rule.Pattern.IsMatch(top))
                    return new ClassificationResult(rule.Category, rule.FormType, ClassificationConfidence.Medium, "TextHeader", rule.Name);
            foreach (var rule in TextHeaderRules)
                if (rule.Pattern.IsMatch(title))
                    return new ClassificationResult(rule.Category, rule.FormType, ClassificationConfidence.Medium, "TextHeader", rule.Name);
        }

        var folderLower = outerCategoryFolder.ToLowerInvariant();
        if (folderLower.Contains("charge"))
            return new ClassificationResult(FilingCategory.Charge, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);
        if (folderLower.Contains("incorporation"))
            return new ClassificationResult(FilingCategory.Constitutional, null, ClassificationConfidence.Low, "OuterFolderFallback", outerCategoryFolder);

        return new ClassificationResult(FilingCategory.Unclassified, null, ClassificationConfidence.Low, "NoMatch", null);
    }

    /// <summary>The file name as the rules read it: lower case, without the MCA document hash prefix ("…v1-"), the extension, and with "_", "-" and
    /// runs of spaces as one space ("Form_AOC-4 CFS" → "form aoc 4 cfs").</summary>
    internal static string NormaliseName(string fileName)
    {
        var n = fileName.ToLowerInvariant();
        n = Extension().Replace(n, "");
        n = HashPrefix().Replace(n, "");
        n = Separators().Replace(n, " ");
        return Whitespace().Replace(n, " ").Trim();
    }

    [GeneratedRegex(@"\.(pdf|xml|xlsx?|docx?|zip|jpe?g|png|tiff?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Extension();

    [GeneratedRegex(@"^[0-9a-f]{32}v\d+(\.dup\d+)?-", RegexOptions.CultureInvariant)]
    private static partial Regex HashPrefix();

    [GeneratedRegex(@"[_\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Separators();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
