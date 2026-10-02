using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Chat;

/// <summary>Category/form-type/lender/year are soft hints — DocumentRetriever applies them but fails open
/// to an unfiltered search if too few results pass the relevance threshold. An SRN match is the one hard
/// filter, since it's a near-unambiguous identifier when it matches a real filing for this request.
/// LexicalTerms are exact-match candidates (case numbers, quoted phrases, statute/section references) that
/// pure semantic similarity tends to under-rank — they drive #194's full-text half of hybrid retrieval, never a
/// filter. OrderOutcomes are the court-order outcomes the question asks about ("orders with a fine/stay") — they
/// route to #195's exact order-outcome lookup, alongside (never instead of) document retrieval.
/// AsksForQuantity routes to the dossier's computed metrics ("M" sources); AsksForCompleteList adds the note that
/// document passages are a top-K sample, not a scan of every document (#193 Finding 2, #338). AsksForRecency
/// ("current status", "last three orders", "next hearing") pulls a named case's newest orders by date, which
/// similarity ranking cannot do (#343). AsksForDecision ("what was decided", "outcome", "was it dismissed") pulls a named
/// case's passages with the most decision language, which similarity under-ranks in long judgements (#353).</summary>
public record QuestionHints(FilingCategory? Category, string? FormTypeKeyword, string? LenderNameKeyword, int? Year, string? SrnMatch,
    IReadOnlyList<string>? LexicalTerms = null, IReadOnlyList<LitigationOrderOutcome>? OrderOutcomes = null,
    bool AsksForQuantity = false, bool AsksForCompleteList = false, bool AsksForRecency = false, bool AsksForDecision = false)
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

        return new QuestionHints(category, formType, lender, year, srn, ExtractLexicalTerms(question), ExtractOrderOutcomes(text),
            AsksForQuantity: QuantityRegex().IsMatch(text), AsksForCompleteList: CompleteListRegex().IsMatch(text),
            AsksForRecency: RecencyRegex().IsMatch(text), AsksForDecision: DecisionQuestionRegex().IsMatch(text));
    }

    /// <summary>Outcome words and whether each is unambiguous on its own. "Stay", "injunction", "dismissed" and
    /// "adjourned" only ever mean one thing in a due-diligence question; "fine", "penalty", "possession" and
    /// "settled" are ordinary English too ("is the company fine?", "possession of the hypothecated stock"), so
    /// those only count when the question is also plainly about court orders.</summary>
    private static readonly (Regex Pattern, LitigationOrderOutcome Outcome, bool SelfEvidentlyLegal)[] OutcomeKeywords =
    [
        (new(@"\b(fine[ds]?|penalt(y|ies)|costs (imposed|awarded)|(imposed|awarded) costs)\b"), LitigationOrderOutcome.FinePenalty, false),
        (new(@"\bpossession\b"), LitigationOrderOutcome.PossessionOrder, false),
        (new(@"\binjunctions?\b"), LitigationOrderOutcome.Injunction, true),
        (new(@"\bdismiss(al|als|ed|es)?\b"), LitigationOrderOutcome.Dismissal, true),
        (new(@"\b(disposed|settled|settlement|withdrawn)\b"), LitigationOrderOutcome.DisposedSettled, false),
        (new(@"\binterim (relief|reliefs|orders?)\b"), LitigationOrderOutcome.InterimRelief, true),
        (new(@"\badjourn(ed|ment|ments)?\b"), LitigationOrderOutcome.AdjournedNoSubstantiveOrder, true)
    ];

    internal static IReadOnlyList<LitigationOrderOutcome> ExtractOrderOutcomes(string lowerText)
    {
        var legalContext = LegalCueRegex().IsMatch(lowerText);
        var outcomes = new List<LitigationOrderOutcome>();
        foreach (var (pattern, outcome, selfEvident) in OutcomeKeywords)
            if ((selfEvident || legalContext) && pattern.IsMatch(lowerText))
                outcomes.Add(outcome);

        // Negation decides which stay outcome is meant: "stay vacated/lifted/set aside" is the opposite of a stay.
        if (StayRegex().IsMatch(lowerText))
            outcomes.Add(StayReversalRegex().IsMatch(lowerText) ? LitigationOrderOutcome.StayVacated : LitigationOrderOutcome.StayGranted);

        return outcomes.Distinct().ToList();
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

    [GeneratedRegex(@"\b(orders?|cases?|court|tribunal|nclt|nclat|drt|drat|judge?ments?|litigation|petitions?|proceedings?|hearings?|bench|appeals?|suits?)\b")]
    private static partial Regex LegalCueRegex();

    /// <summary>Questions a computed metric can answer: counts, totals, ratios, shares, trends.</summary>
    [GeneratedRegex(@"\b(how (many|much)|number of|count|total|ratio|percent(age)?|average|growth|trend|cagr|share of|proportion|utili[sz]ation|exposure|outstanding)\b|%")]
    private static partial Regex QuantityRegex();

    /// <summary>Questions asking for every item rather than an example or a passage. Every count form ("how many",
    /// "count", "number of", "total number of") is included: a count built from retrieved passages is just as
    /// incomplete as a list built from them. Over-matching only adds the S note, which the prompt tells the model to
    /// use solely when its answer relies on D/L passages, so an exact M/F/O count is never called partial.</summary>
    [GeneratedRegex(@"\b(list|enumerate|every|each of|all (the|of|\w+s)|how many|count(s|ing)?|number of|complete list|full list|which (ones|all))\b")]
    private static partial Regex CompleteListRegex();

    [GeneratedRegex(@"\b(latest|last|recent(ly)?|current(ly)?|status|next (hearing|date)|upcoming|so far|to date)\b")]
    private static partial Regex RecencyRegex();

    [GeneratedRegex(@"\b(decid(e|ed|ing)|decisions?|outcomes?|results?|rulings?|verdict|held|disposed|disposal|dismiss(ed|al)?|allowed|rejected|granted|what happened|fate)\b")]
    private static partial Regex DecisionQuestionRegex();

    [GeneratedRegex(@"\bstay(s|ed)?\b")]
    private static partial Regex StayRegex();

    [GeneratedRegex(@"\b(vacat\w*|lift\w*|set aside|refus\w*|revok\w*|withdr[ae]w\w*)\b")]
    private static partial Regex StayReversalRegex();
}
