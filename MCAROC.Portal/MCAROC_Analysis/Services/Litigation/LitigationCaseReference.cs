using System.Globalization;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>A case reference reduced to what both a user and a court reliably agree on: the serial and the year,
/// plus the case-type letters as a tie-breaker. A user writes "TP 255/2019"; the order sheet says "TP No.
/// 255/CTB/2019"; the litigation data may store "TP(IB) 255/CTB/2019", or just "255" with the year in
/// <see cref="LitigationCase.CaseYear"/> — #343 found the user's form appears verbatim nowhere in the real corpus,
/// so full-text search can't anchor the case and this match is done against the structured case record instead.</summary>
public sealed partial record LitigationCaseReference(string TypeLetters, string Serial, string Year)
{
    /// <summary>Every case reference in a question: optional type prefix, optional "No.", a serial, an optional
    /// bench code ("CTB", "KB", "ND"), then the year, joined by "/" or "of" — e.g. "TP 255/2019", "IA No. 45 of
    /// 2021", "Company Appeal (AT) (Ins) No. 935 of 2023", "CP(IB) 593/KB/2017".</summary>
    public static IReadOnlyList<LitigationCaseReference> Extract(string text)
    {
        var refs = new List<LitigationCaseReference>();
        foreach (Match m in ReferenceRegex().Matches(text))
        {
            var r = new LitigationCaseReference(Letters(CaseTypeTokens(m.Groups["type"].Value)), TrimSerial(m.Groups["serial"].Value), m.Groups["year"].Value);
            if (!refs.Contains(r)) refs.Add(r);
        }
        return refs;
    }

    /// <summary>The stored case's own reference, from <see cref="LitigationCase.CaseNumber"/> when it carries a
    /// year, else its lone serial plus <see cref="LitigationCase.CaseYear"/>; null when no serial/year can be told
    /// apart. Type letters come from the case number's prefix, falling back to <see cref="LitigationCase.CaseType"/>.</summary>
    public static LitigationCaseReference? Of(LitigationCase c)
    {
        var number = c.CaseNumber ?? "";
        var fromNumber = Extract(number).FirstOrDefault();
        if (fromNumber is not null)
            return fromNumber.TypeLetters.Length > 0 ? fromNumber : fromNumber with { TypeLetters = Letters(c.CaseType ?? "") };

        if (string.IsNullOrWhiteSpace(c.CaseYear) || !YearOnlyRegex().IsMatch(c.CaseYear.Trim())) return null;
        var year = c.CaseYear.Trim();
        var serials = SerialRegex().Matches(number).Select(m => m.Value).Where(s => s != year).Select(TrimSerial).ToList();
        if (serials.Count != 1) return null;
        var prefix = Letters(LeadingTypeRegex().Match(number).Value);
        return new LitigationCaseReference(prefix.Length > 0 ? prefix : Letters(c.CaseType ?? ""), serials[0], year);
    }

    /// <summary>Same serial and year; type letters must not contradict ("TP" matches "TPIB", "CP" matches "CPIB",
    /// "TP" never matches "IA"). Either side having no letters is not a contradiction.</summary>
    public bool Matches(LitigationCaseReference other) =>
        Serial == other.Serial && Year == other.Year
        && (TypeLetters.Length == 0 || other.TypeLetters.Length == 0
            || TypeLetters.StartsWith(other.TypeLetters, StringComparison.Ordinal)
            || other.TypeLetters.StartsWith(TypeLetters, StringComparison.Ordinal));

    /// <summary>The request's cases a question refers to. When letters were given but no case agrees on them, a
    /// serial+year that identifies exactly one case still counts — a user's "CA 935/2023" for a stored "Company
    /// Appeal (AT)(Ins) 935/2023" — but an ambiguous one never does.</summary>
    public static IReadOnlyList<LitigationCase> FindReferencedCases(string question, IEnumerable<LitigationCase> cases)
    {
        var asked = Extract(question);
        if (asked.Count == 0) return [];
        var withRefs = cases.Select(c => (Case: c, Ref: Of(c))).Where(x => x.Ref is not null).ToList();
        var found = new List<LitigationCase>();
        foreach (var q in asked)
        {
            var sameNumber = withRefs.Where(x => x.Ref!.Serial == q.Serial && x.Ref.Year == q.Year).ToList();
            var agreeing = sameNumber.Where(x => q.Matches(x.Ref!)).ToList();
            var picked = agreeing.Count > 0 ? agreeing : sameNumber.Count == 1 ? sameNumber : [];
            foreach (var x in picked)
                if (!found.Contains(x.Case)) found.Add(x.Case);
        }
        return found;
    }

    /// <summary>Order and hearing dates arrive as text ("09-01-2025" in the litigation data); null when unparseable,
    /// so callers can sort undated orders last rather than guess.</summary>
    public static DateOnly? ParseDate(string? value) =>
        !string.IsNullOrWhiteSpace(value) && DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    private static readonly string[] DateFormats =
        ["dd-MM-yyyy", "d-M-yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy", "yyyy-MM-dd", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy"];

    /// <summary>Drops a capitalised sentence word that merely precedes the case type ("Summarise TP 255/2019" → "TP"):
    /// leading Title-case words go when an all-caps type token ("TP", "IA", "CP(IB)") follows them. "Company Appeal
    /// (AT) (Ins)" has no such token and is kept whole.</summary>
    private static string CaseTypeTokens(string type)
    {
        var tokens = type.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstCaps = Array.FindIndex(tokens, t => AllCapsTypeRegex().IsMatch(t));
        return firstCaps > 0 && tokens[..firstCaps].All(t => TitleWordRegex().IsMatch(t))
            ? string.Join(' ', tokens[firstCaps..]) : type;
    }

    private static string Letters(string s)
    {
        var letters = new string(s.Where(char.IsLetter).ToArray()).ToUpperInvariant();
        return letters == "NO" || letters.EndsWith("NO", StringComparison.Ordinal) && letters.Length > 2 ? letters[..^2] : letters;
    }

    private static string TrimSerial(string s) => s.TrimStart('0') is { Length: > 0 } t ? t : s;

    /// <summary>The type is up to four capitalised or bracketed tokens ("TP", "TP(IB)", "Company Appeal (AT) (Ins)")
    /// immediately before the number, so lower-case question words ("status of") never join it.</summary>
    [GeneratedRegex(@"(?<type>(?:\(?[A-Z][A-Za-z.]*\)?(?:\([A-Za-z.]+\))?\s*){1,4})?(?:\bNo\.?\s*)?(?<serial>\d{1,6})\s*(?:/\s*[A-Za-z]{1,4}\s*)?(?:/|\bof\b)\s*(?<year>(?:19|20)\d{2})\b")]
    private static partial Regex ReferenceRegex();

    [GeneratedRegex(@"\d{1,6}")]
    private static partial Regex SerialRegex();

    [GeneratedRegex(@"^\s*(?:19|20)\d{2}\s*$")]
    private static partial Regex YearOnlyRegex();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z.()\s]*")]
    private static partial Regex LeadingTypeRegex();

    [GeneratedRegex(@"^[A-Z]{2,}(\(|$)")]
    private static partial Regex AllCapsTypeRegex();

    [GeneratedRegex(@"^[A-Z][a-z]+$")]
    private static partial Regex TitleWordRegex();
}
