using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.PropertyParticulars;

/// <summary>What a passage says about the property.</summary>
public enum InstrumentPassageKind
{
    /// <summary>The short particulars of the property charged (CHG-1 item 13(a)), including its address and location.</summary>
    Particulars,
    /// <summary>A row or rows of the structured property table (CHG-1 item 13(b)): plot/unit, survey no., locality, area…</summary>
    PropertyTable,
    /// <summary>A schedule, annexure or description of the mortgaged/charged property in a deed or instrument.</summary>
    Schedule,
    /// <summary>A statement that property is not registered in the company's name, with the name it is registered in.</summary>
    Ownership
}

/// <summary>One passage quoted from a document: its words exactly as in the document's text, the kind the model labelled it
/// with, and the page it starts on (and ends on, for a schedule that runs over a page).</summary>
public sealed record InstrumentPassage(InstrumentPassageKind Kind, string Text, int Page, int EndPage);

public sealed record ChargeInstrumentAiResult(IReadOnlyList<InstrumentPassage> Passages);

public sealed record ChargeInstrumentValidation(bool IsAccepted, ChargeInstrumentAiResult? Result, IReadOnlyList<string> Rejected, string? FailureReason);

/// <summary>#364 part 2: prompt, grounding and page lookup for quoting property passages out of a charge document. The model
/// copies; it never writes, completes or corrects. <see cref="Validate"/> keeps a passage only if its words occur, in order,
/// in the document's text — so a hallucinated survey number or flat number can never be shown (flat 305 is not flat 315). The
/// page of each passage is worked out here from the document's own page markers, never taken from the model.</summary>
public static partial class ChargeInstrumentAi
{
    /// <summary>Bump to re-read every document (a prompt or rule change).</summary>
    public const string PromptVersion = "1.1";
    internal const int MaxPassages = 120;
    internal const int MaxPassageChars = 30_000;
    internal const string PromptMarker = "You are quoting the property description out of one document filed with an Indian ROC charge";

    public static string BuildPrompt(string documentText) => $$"""
        {{PromptMarker}} (MCA Form CHG-1/CHG-9, Form 8/13/17, or an attached deed, agreement, instrument or letter), for a BFSI
        property due diligence analyst. Find every passage that describes the property or assets charged, and quote it.
        Quote text that IDENTIFIES or DESCRIBES the assets: an address or location, survey/plot/gat/CTS/unit/flat numbers, areas, the
        name of a building or project, a list of equipment (make, model, serial, chassis or engine numbers), shares pledged (company,
        number and class), a description of receivables or stock. Do NOT quote clauses about the charge itself (obligations, rights,
        remedies, "the charge shall continue in force"), definitions, or words that merely point at the assets without describing them
        ("the said Asset", "the Hypothecated Property", "as per Schedule I").
        STRICT RULES:
        - COPY each passage exactly as written in the text: same words, same numbers, same spelling and punctuation. Never
          summarise, translate, convert units, correct spelling, complete a number, or join passages that are not adjacent.
        - A passage is a continuous stretch of the text. Keep a schedule of many units or survey numbers whole, in one passage:
          do not split a list or a table into one passage per line.
        - kind is one of: Particulars (the short "particulars of the property or asset(s) charged", including its address and location),
          PropertyTable (a row or rows of a table of plot/unit/survey/locality/area details of the property), Schedule (a schedule,
          annexure or description of the mortgaged or charged property in a deed or instrument), Ownership (a statement that property is
          not registered in the company's name, with the name it is registered in).
        - Do NOT quote: the company's own registered-office or correspondence address, names and addresses of signatories or
          witnesses, form instructions or headings, stamp-duty or registration endorsements, or boilerplate that names no property.
        - If the particulars only refer elsewhere ("as per Schedule I attached"), quote the schedule itself if it appears in this text.
        - If the text contains no description of any property, return an empty list. Do not guess.
        Respond only with JSON of this shape: {"passages":[{"kind":"Schedule","text":"<the passage, copied exactly>"}]}
        The text below has "--- Page N (native) ---" or "--- Page N (OCR) ---" markers between pages; do not copy the markers.
        Text:
        {{documentText}}
        """;

    // ── Page lookup ──

    [GeneratedRegex(@"^--- Page (?<n>\d+) \((?:native|OCR)\) ---\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PageMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>The document text without its page markers, whitespace collapsed to single spaces, and for each page the offset
    /// at which it starts in that text. A text with no markers is one page (1).</summary>
    internal sealed class PageIndex
    {
        public string Text { get; }
        private readonly List<(int Offset, int Page)> _starts = [];

        public PageIndex(string documentText)
        {
            var sb = new StringBuilder();
            var markers = PageMarker().Matches(documentText);
            if (markers.Count == 0)
            {
                Text = Whitespace().Replace(documentText, " ").Trim();
                _starts.Add((0, 1));
                return;
            }
            var pos = 0;
            var page = 1;
            void Append(string chunk, int forPage)
            {
                var normalised = Whitespace().Replace(chunk, " ").Trim();
                if (normalised.Length == 0) return;
                if (sb.Length > 0) sb.Append(' ');
                _starts.Add((sb.Length, forPage));
                sb.Append(normalised);
            }
            foreach (Match m in markers)
            {
                Append(documentText[pos..m.Index], page);
                page = int.Parse(m.Groups["n"].Value);
                pos = m.Index + m.Length;
            }
            Append(documentText[pos..], page);
            Text = sb.ToString();
            if (_starts.Count == 0) _starts.Add((0, 1));
        }

        public int PageAt(int offset)
        {
            var page = _starts[0].Page;
            foreach (var (o, p) in _starts)
            {
                if (o > offset) break;
                page = p;
            }
            return page;
        }
    }

    /// <summary>The model sometimes copies the page markers into a passage; they aren't document text, so they are removed
    /// before the passage is looked up.</summary>
    internal static string Normalise(string passage) => Whitespace().Replace(PageMarker().Replace(passage, " "), " ").Trim();

    public static ChargeInstrumentValidation Validate(string rawJson, string documentText)
    {
        Response? response;
        try { response = JsonSerializer.Deserialize<Response>(PropertyParticularsAi.UnwrapSingleElementArray(rawJson), JsonOptions); }
        catch (JsonException ex) { return Failed($"Invalid JSON: {ex.Message}"); }
        if (response?.Passages is null) return Failed("Response has no passages array.");
        if (response.Passages.Count > MaxPassages) return Failed($"Response returned {response.Passages.Count} passages (max {MaxPassages}).");

        var index = new PageIndex(documentText);
        var rejected = new List<string>();
        var kept = new List<InstrumentPassage>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < response.Passages.Count; i++)
        {
            var p = response.Passages[i];
            if (p is null) return Failed($"passages[{i}] is null.");
            if (!Enum.TryParse<InstrumentPassageKind>(p.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                rejected.Add($"passages[{i}]: unsupported kind '{p.Kind}' — dropped");
                continue;
            }
            var text = Normalise(p.Text ?? "");
            if (text.Length < 8)
            {
                rejected.Add($"passages[{i}]: empty or too short — dropped");
                continue;
            }
            if (text.Length > MaxPassageChars)
            {
                rejected.Add($"passages[{i}]: longer than {MaxPassageChars} characters — dropped");
                continue;
            }
            var at = FindQuote(index.Text, text);
            if (at < 0)
            {
                rejected.Add($"passages[{i}]: not a verbatim quote of the document — dropped");
                continue;
            }
            if (IsReferenceOnly(text))
            {
                rejected.Add($"passages[{i}]: refers to the property without describing it — dropped");
                continue;
            }
            if (!seen.Add(text)) continue; // the same words quoted twice
            kept.Add(new InstrumentPassage(kind, text, index.PageAt(at), index.PageAt(at + text.Length - 1)));
        }
        return new ChargeInstrumentValidation(true, new ChargeInstrumentAiResult(kept), rejected, null);
    }

    /// <summary>A short fragment that only points at the assets ("the said Asset", "one or more of the said Asset more particularly
    /// described hereunder"): it names nothing, so it is not a description. Anything with a digit is kept, as is anything long enough
    /// to be more than a pointer.</summary>
    internal static bool IsReferenceOnly(string text) =>
        text.Length < 80 && !text.Any(char.IsDigit) && ReferenceWords().IsMatch(text);

    [GeneratedRegex(@"\b(said|aforesaid|hereunder|hereinabove|hereinafter|abovementioned|above[- ]mentioned)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ReferenceWords();

    /// <summary>Where the quote sits in the document text, as a whole: it must start and end on identifier boundaries. A quote
    /// that stops inside a longer word or number ("Flat No. 30" out of "Flat No. 305"), or at the slash, hyphen, dot or letter that
    /// continues an identifier ("12" out of "12/3", "30" out of "30A"), is part of a different identifier, not a quote of this one,
    /// and is not found. The same goes for a quote that starts inside one ("05" out of "305"). Case and spacing don't matter.</summary>
    internal static int FindQuote(string text, string quote)
    {
        for (var at = text.IndexOf(quote, StringComparison.OrdinalIgnoreCase); at >= 0;
            at = at + 1 < text.Length ? text.IndexOf(quote, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (StartsOnBoundary(text, at) && EndsOnBoundary(text, at + quote.Length)
                && !SplitsIdentifierAcrossSpace(text, at, at + quote.Length)) return at;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    /// <summary>A separator that joins the two halves of one identifier when a word character sits on each side of it.</summary>
    private static bool IsJoiner(char c) => c is '/' or '-' or '.' or '\'' or '_' or '(' or ')';

    private static bool EndsOnBoundary(string text, int end)
    {
        if (end >= text.Length) return true;
        // A quote that ends on the joiner itself ("12/" out of "12/3") has stopped inside the identifier too.
        if (!IsWordChar(text[end - 1])) return !(IsJoiner(text[end - 1]) && IsWordChar(text[end]));
        var next = text[end];
        if (IsWordChar(next)) return false; // stops inside a word or number
        return !(IsJoiner(next) && end + 1 < text.Length && IsWordChar(text[end + 1]));
    }

    private static bool StartsOnBoundary(string text, int start)
    {
        if (start <= 0) return true;
        if (!IsWordChar(text[start])) return !(IsJoiner(text[start]) && IsWordChar(text[start - 1]));
        var previous = text[start - 1];
        if (IsWordChar(previous)) return false; // starts inside a word or number
        return !(IsJoiner(previous) && start - 2 >= 0 && IsWordChar(text[start - 2]));
    }

    /// <summary>Whitespace — a space or a line break from the PDF — can sit around the slash or hyphen of an identifier ("12 / 3",
    /// "12/\n3"). A quote that stops on one side of such a joiner has cut the identifier just the same. A hyphen or slash joins two word
    /// characters when one is a digit ("A - 305", "305 - A", "12 / 3"; "Baner / Taluka" is two places, not one identifier).</summary>
    private static bool SplitsIdentifierAcrossSpace(string text, int start, int end)
    {
        static bool Joins(char left, char joiner, char right) => joiner switch
        {
            '/' => IsWordChar(left) && IsWordChar(right) && (char.IsDigit(left) || char.IsDigit(right)),
            '-' => IsWordChar(left) && IsWordChar(right) && (char.IsDigit(left) || char.IsDigit(right)),
            _ => false
        };
        int Back(int i) { while (i >= 0 && char.IsWhiteSpace(text[i])) i--; return i; }
        int Fwd(int i) { while (i < text.Length && char.IsWhiteSpace(text[i])) i++; return i; }

        // The end: the quote's last character, and what follows it after any whitespace.
        var last = Back(end - 1);
        var after = Fwd(end);
        if (last >= 0 && after < text.Length)
        {
            // …12 | / 3 — the quote stops before a joiner that is followed by the rest of the identifier
            if (text[after] is '/' or '-')
            {
                var rest = Fwd(after + 1);
                if (rest < text.Length && Joins(text[last], text[after], text[rest])) return true;
            }
            // …12 / | 3 — the quote ends on the joiner
            if (text[last] is '/' or '-')
            {
                var before = Back(last - 1);
                if (before >= 0 && Joins(text[before], text[last], text[after])) return true;
            }
        }
        // The start, mirrored: what precedes the quote's first character after any whitespace.
        var first = Fwd(start);
        var preceding = Back(start - 1);
        if (first < text.Length && preceding >= 0)
        {
            // 12 / | 3… — the quote starts after a joiner that follows the rest of the identifier
            if (text[preceding] is '/' or '-')
            {
                var rest = Back(preceding - 1);
                if (rest >= 0 && Joins(text[rest], text[preceding], text[first])) return true;
            }
            // 12 | / 3… — the quote starts on the joiner
            if (text[first] is '/' or '-')
            {
                var next = Fwd(first + 1);
                if (next < text.Length && Joins(text[preceding], text[first], text[next])) return true;
            }
        }
        return false;
    }

    private static ChargeInstrumentValidation Failed(string reason) => new(false, null, [], reason);

    public static string Serialize(ChargeInstrumentAiResult result) => JsonSerializer.Serialize(result, JsonOptions);

    public static ChargeInstrumentAiResult? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ChargeInstrumentAiResult>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record Response(List<RawPassage?>? Passages);
    private sealed record RawPassage(string? Kind, string? Text);
}
