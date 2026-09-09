using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Category/form-type/lender/year are soft hints — DocumentRetriever applies them but fails open
/// to an unfiltered search if too few results pass the relevance threshold. An SRN match is the one hard
/// filter, since it's a near-unambiguous identifier when it matches a real filing for this request.</summary>
public record QuestionHints(FilingCategory? Category, string? FormTypeKeyword, string? LenderNameKeyword, int? Year, string? SrnMatch)
{
    public bool HasSoftHints => Category is not null || FormTypeKeyword is not null || LenderNameKeyword is not null;
}

/// <summary>Deterministic keyword/regex extraction (same style as FilingClassifier) — not an AI router.
/// Cheap, high-value narrowing for an obvious question like "what did the Kotak charge instrument say?",
/// without the cost/latency of a classification model call.</summary>
public static partial class QuestionHintExtractor
{
    private static readonly (string Keyword, FilingCategory Category)[] CategoryKeywords =
    [
        ("charge", FilingCategory.Charge), ("lien", FilingCategory.Charge), ("mortgage", FilingCategory.Charge),
        ("hypothecation", FilingCategory.Charge), ("security", FilingCategory.Charge),
        ("gst", FilingCategory.Compliance), ("epfo", FilingCategory.Compliance), ("provident fund", FilingCategory.Compliance),
        ("director", FilingCategory.Compliance), ("auditor", FilingCategory.Compliance),
        ("litigation", FilingCategory.Compliance), ("lawsuit", FilingCategory.Compliance), ("court case", FilingCategory.Compliance),
        ("memorandum", FilingCategory.Constitutional), ("articles of association", FilingCategory.Constitutional),
        ("incorporation", FilingCategory.Constitutional), ("main object", FilingCategory.Constitutional),
        ("balance sheet", FilingCategory.Financial), ("xbrl", FilingCategory.Financial)
    ];

    private static readonly string[] FormTypeTokens =
        ["CHG-1", "CHG-4", "MGT-14", "MGT-7", "ADT-1", "ADT-3", "INC-22", "INC-28", "PAS-3", "AOC-4", "FORM-23", "23AC", "23ACA"];

    public static QuestionHints Extract(string question, IReadOnlyList<string> knownLenderNames, IReadOnlyList<string> knownSrns)
    {
        var text = question.ToLowerInvariant();

        FilingCategory? category = null;
        foreach (var (keyword, cat) in CategoryKeywords)
            if (text.Contains(keyword, StringComparison.Ordinal)) { category = cat; break; }

        var formType = FormTypeTokens.FirstOrDefault(t => text.Contains(t.ToLowerInvariant(), StringComparison.Ordinal));

        var lender = knownLenderNames.FirstOrDefault(l =>
            !string.IsNullOrWhiteSpace(l) && l.Length >= 3 && text.Contains(l.ToLowerInvariant(), StringComparison.Ordinal));

        var yearMatch = YearRegex().Match(question);
        int? year = yearMatch.Success ? int.Parse(yearMatch.Value) : null;

        var srn = knownSrns.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s) && Regex.IsMatch(question, $@"\b{Regex.Escape(s)}\b"));

        return new QuestionHints(category, formType, lender, year, srn);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();
}
