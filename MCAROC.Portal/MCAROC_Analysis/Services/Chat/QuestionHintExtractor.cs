using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Category/form-type/lender/year are soft hints — DocumentRetriever applies them but fails open
/// to an unfiltered search if too few results pass the relevance threshold. An SRN match is the one hard
/// filter, since it's a near-unambiguous identifier when it matches a real filing for this request.
/// LexicalTerms are exact-match candidates (case numbers, quoted phrases, statute/section references) that
/// pure semantic similarity tends to under-rank — they drive #194's full-text half of hybrid retrieval, never a
/// filter.</summary>
public record QuestionHints(FilingCategory? Category, string? FormTypeKeyword, string? LenderNameKeyword, int? Year, string? SrnMatch,
    IReadOnlyList<string>? LexicalTerms = null)
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

        return new QuestionHints(category, formType, lender, year, srn, ExtractLexicalTerms(question));
    }

    /// <summary>Caps how many exact-match phrases one question can OR together — a question quoting a dozen
    /// fragments is better served by the semantic search than by a sprawling full-text condition.</summary>
    internal const int MaxLexicalTerms = 5;

    internal static IReadOnlyList<string> ExtractLexicalTerms(string question)
    {
        var terms = new List<string>();
        void Add(string raw)
        {
            var term = WhitespaceRegex().Replace(raw.Trim(), " ");
            if (term.Length >= 2 && !terms.Contains(term, StringComparer.OrdinalIgnoreCase))
                terms.Add(term);
        }

        foreach (Match m in QuotedPhraseRegex().Matches(question)) Add(m.Groups["phrase"].Value);
        foreach (Match m in CaseNumberRegex().Matches(question)) Add(m.Value);
        foreach (Match m in SectionReferenceRegex().Matches(question)) Add(m.Value);

        return terms.Take(MaxLexicalTerms).ToList();
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"[""\u201C\u201D](?<phrase>[^""\u201C\u201D]{2,120})[""\u201C\u201D]")]
    private static partial Regex QuotedPhraseRegex();

    /// <summary>Court case numbers as they appear in Indian order sheets: an upper-case case-type prefix
    /// (<c>TP</c>, <c>WP</c>, <c>CP(IB)</c>, <c>O.S.</c>, <c>IA</c>…), an optional "No.", a serial and a year
    /// joined by "/" or "of" — e.g. <c>TP 255/2019</c>, <c>WP/11227/2019</c>, <c>CP(IB) No. 123/2020</c>,
    /// <c>IA No. 45 of 2021</c>. Case-sensitive on the prefix so ordinary words never match.</summary>
    [GeneratedRegex(@"\b[A-Z][A-Z.()]{0,14}(?:\s*No\.?)?[\s/.-]*\d{1,6}\s*(?:/|\bof\b)\s*(?:19|20)\d{2}\b")]
    private static partial Regex CaseNumberRegex();

    /// <summary>Statute references — <c>Section 7</c>, <c>section 138</c>, <c>Sec. 29A</c>, <c>Section 13(2)</c>.</summary>
    [GeneratedRegex(@"\b(?:[Ss]ection|SECTION|[Ss]ec\.?)\s*\d{1,4}[A-Z]?(?:\(\w{1,4}\))*")]
    private static partial Regex SectionReferenceRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
