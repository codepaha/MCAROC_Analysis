using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Normalisation;

/// <summary>Where the year of a case came from. Only <see cref="Number"/> and <see cref="Column"/> are explicit: the others are inferred
/// and are never enough on their own to make a case's identity complete (plan section 3.3).</summary>
public enum YearSource { None, Number, Column, InferredFilingAndCnr, InferredFilingOnly, InferredCnrOnly }

/// <summary>How much of <c>TYPE/NO/YEAR</c> is known from the source's own text. <b>Complete</b> = type, number and an explicit year.
/// <b>Partial</b> = a number plus something missing, unknown or inferred. <b>Unresolved</b> = no case number could be read.
/// This is display quality, not identity: a shared case key is never minted from this (plan section 3.3).</summary>
public enum CaseNumberCompleteness { Complete, Partial, Unresolved }

/// <param name="CaseNumber">The case-number text as the source gave it.</param>
/// <param name="CaseType">The source's separate case-type column, if it has one.</param>
/// <param name="CaseYear">The source's separate year column, if it has one.</param>
/// <param name="FilingDate">Used only to infer a missing year.</param>
/// <param name="Cnr">Used only to infer a missing year (the last four digits of a 16-character CNR).</param>
public sealed record CaseNumberInput(string? CaseNumber, string? CaseType = null, string? CaseYear = null, string? FilingDate = null, string? Cnr = null);

/// <param name="TypeCode">Canonical short type (<c>OA</c>, <c>CRM-M</c>, <c>SLP(C)</c>), or null when unknown - never guessed.</param>
/// <param name="No">The case number without leading zeros.</param>
/// <param name="Display">The number in the one fixed format, <c>TYPE/NO/YEAR</c>, with any unknown part omitted; the text as filed when nothing could be read.</param>
/// <param name="Note">A short muted note for the UI ("type not stated"), or null when nothing is missing.</param>
/// <param name="ConnectedMatters">Further numbers that arrived in the same source string; the first is the primary.</param>
/// <param name="ActHint">An act the type text identifies (for example section 138 of the Negotiable Instruments Act), only ever used to fill an empty act.</param>
/// <param name="PortalNumber">The portal's own full form when the source gave one (<c>DC/497/CC/599/2017</c>); needed for lookups, not for display.</param>
/// <param name="Notes">Machine-readable reasons, for tests and diagnostics.</param>
public sealed record CaseNumberParts(
    string? TypeCode, string? No, int? Year, YearSource YearSource, CaseNumberCompleteness Completeness, string Display, string? Note,
    IReadOnlyList<string> ConnectedMatters, string? ActHint, string? PortalNumber, IReadOnlyList<string> Notes, string Raw);

/// <summary>Turns the several shapes in which sources spell a case number into one structure and one display format (<c>TYPE/NO/YEAR</c>).
/// Free and deterministic; no network. It reads <i>display</i> parts only - a shared case key is minted elsewhere, from the identity a court
/// portal returns (plan section 3.3).</summary>
public static partial class CaseNumberNormaliser
{
    public static CaseNumberParts Parse(CaseNumberInput input)
    {
        var raw = (input.CaseNumber ?? string.Empty).Trim();
        var notes = new List<string>();
        var connected = new List<string>();
        string? typeFromNumber = null, no = null, yearText = null, portalNumber = null, actHint = null;

        var text = raw.Replace("\\", "");
        text = VerifiedOn().Replace(text, "").Trim();

        // Portal full forms: DC/497/CC/599/2017 (district commission code / serial / type / number / year) and NC/RP/836/2005.
        var full = PortalDistrict().Match(text);
        if (!full.Success) full = PortalNational().Match(text);
        if (full.Success)
        {
            typeFromNumber = full.Groups["type"].Value; no = StripZeros(full.Groups["no"].Value); yearText = full.Groups["year"].Value;
            portalNumber = text;
        }
        else if (BenchForm().Matches(text) is { Count: > 0 } bench)
        {
            // NCLT/NCLAT: TPNo.93/CTB/2019 - the bench sits between number and year, and several numbers can be run together with no separator.
            var first = bench[0];
            typeFromNumber = first.Groups["type"].Value; no = StripZeros(first.Groups["no"].Value); yearText = first.Groups["year"].Value;
            notes.Add("bench:" + first.Groups["bench"].Value.ToUpperInvariant());
            foreach (Match m in bench.Skip(1)) connected.Add(Collapse(m.Value));
        }
        else
        {
            // Supreme Court style lists separate connected matters with commas; the first is the primary.
            var segments = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var primary = segments.Length > 0 ? segments[0] : text;
            foreach (var extra in segments.Skip(1)) connected.Add(extra);

            var cleaned = NoToken().Replace(primary, " ").Trim();
            cleaned = DashBeforeSlash().Replace(cleaned, "/");
            Match m;
            if ((m = TypeDashNameSlash().Match(cleaned)).Success)          // CS - CIVIL SUIT/1411/2019
            {
                typeFromNumber = m.Groups["type"].Value; no = StripZeros(m.Groups["no"].Value); yearText = m.Groups["year"].Value;
                actHint = ActFromName(m.Groups["name"].Value);
            }
            else if ((m = TypeNoYear().Match(cleaned)).Success)             // OA/413/2022, SLP(C) 6459/2019
            {
                typeFromNumber = m.Groups["type"].Value.Trim(); no = StripZeros(m.Groups["no"].Value); yearText = m.Groups["year"].Value;
            }
            else if ((m = TypeDashNoYear().Match(cleaned)).Success)         // Company Appeal(AT)(Ins) - 383/2020
            {
                typeFromNumber = m.Groups["type"].Value.Trim(); no = StripZeros(m.Groups["no"].Value); yearText = m.Groups["year"].Value;
            }
            else if ((m = NoYear().Match(cleaned)).Success)                 // 292/2016
            {
                no = StripZeros(m.Groups["no"].Value); yearText = m.Groups["year"].Value;
            }
            else if ((m = NumberOnly().Match(cleaned)).Success)             // 10555
            {
                no = StripZeros(m.Groups["no"].Value);
            }
        }
        if (connected.Count > 0) notes.Add("connected:" + connected.Count);

        // Type: the number's own prefix wins; otherwise the type column - unless that column actually holds a number ("SLP(Crl) No. 004537 - ").
        string? typeCode = null;
        var typeSource = typeFromNumber;
        if (typeSource is null && !string.IsNullOrWhiteSpace(input.CaseType))
        {
            if (LooksLikeNumberText().IsMatch(input.CaseType)) notes.Add("type-column-was-a-number");
            else typeSource = input.CaseType;
        }
        if (!string.IsNullOrWhiteSpace(typeSource))
        {
            typeCode = CaseTypeCodes.Resolve(typeSource);
            if (typeCode is null) notes.Add("type-unknown:" + Collapse(typeSource));
        }
        else if (no is not null) notes.Add("type-missing");

        // Year: explicit in the number, else the year column, else inferred from the filing date and the CNR.
        int? year = null; var yearSource = YearSource.None;
        if (yearText is not null) { year = int.Parse(yearText); yearSource = YearSource.Number; }
        else if (no is not null)
        {
            var column = input.CaseYear?.Trim();
            if (column is { Length: 4 } && column.All(char.IsDigit)) { year = int.Parse(column); yearSource = YearSource.Column; }
            else
            {
                var filing = YearIn(input.FilingDate);
                var cnr = YearFromCnr(input.Cnr);
                if (filing is not null && cnr is not null && filing == cnr) { year = filing; yearSource = YearSource.InferredFilingAndCnr; }
                else if (filing is not null) { year = filing; yearSource = YearSource.InferredFilingOnly; }
                else if (cnr is not null) { year = cnr; yearSource = YearSource.InferredCnrOnly; }
                else notes.Add("year-missing");
                if (yearSource is YearSource.InferredFilingAndCnr or YearSource.InferredFilingOnly or YearSource.InferredCnrOnly)
                    notes.Add("year-inferred:" + yearSource);
            }
        }

        var completeness = no is null ? CaseNumberCompleteness.Unresolved
            : typeCode is not null && year is not null && yearSource is YearSource.Number or YearSource.Column ? CaseNumberCompleteness.Complete
            : CaseNumberCompleteness.Partial;
        if (no is null) notes.Add("no-number-found");

        var display = no is null
            ? (raw.Length == 0 ? string.Empty : raw)
            : string.Join('/', new[] { typeCode, no, year?.ToString() }.Where(p => !string.IsNullOrEmpty(p)));
        return new CaseNumberParts(typeCode, no, year, yearSource, completeness, display, NoteFor(no, typeCode, year, yearSource),
            connected, actHint, portalNumber, notes, raw);
    }

    private static string? NoteFor(string? no, string? type, int? year, YearSource yearSource)
    {
        if (no is null) return raw_not_recognised;
        var parts = new List<string>();
        var missing = new List<string>();
        if (type is null) missing.Add("type");
        if (year is null) missing.Add("year");
        if (missing.Count > 0) parts.Add(string.Join(" and ", missing) + " not stated");
        if (yearSource is YearSource.InferredFilingAndCnr or YearSource.InferredFilingOnly or YearSource.InferredCnrOnly)
            parts.Add("year inferred from " + (yearSource == YearSource.InferredCnrOnly ? "the CNR" : "the filing date"));
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    private const string raw_not_recognised = "number format not recognised";

    private static string? ActFromName(string name)
    {
        var m = Section138().Match(name);
        return m.Success ? "Negotiable Instruments Act s.138" : null;
    }

    private static int? YearIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = FourDigitYear().Match(value);
        return m.Success ? int.Parse(m.Value) : null;
    }

    private static int? YearFromCnr(string? cnr)
    {
        if (string.IsNullOrWhiteSpace(cnr)) return null;
        var c = cnr.Trim().ToUpperInvariant();
        return c.Length == 16 && c[^4..].All(char.IsDigit) ? int.Parse(c[^4..]) : null;
    }

    private static string StripZeros(string digits) { var t = digits.TrimStart('0'); return t.Length == 0 ? "0" : t; }
    private static string Collapse(string s) => Whitespace().Replace(s.Trim(), " ");

    [GeneratedRegex(@",?\s*\(Verified On [^)]*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex VerifiedOn();
    [GeneratedRegex(@"^[A-Z]{2}/\d+/(?<type>[A-Z]{1,6})/(?<no>\d+)/(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex PortalDistrict();
    [GeneratedRegex(@"^(?:NC|SC)/(?<type>[A-Z]{1,6})/(?<no>\d+)/(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex PortalNational();
    [GeneratedRegex(@"(?<type>[A-Za-z][A-Za-z.()\s]*?)\s*No\.?\s*(?<no>\d+)\s*/\s*(?<bench>[A-Za-z]+)\s*/\s*(?<year>\d{4})", RegexOptions.CultureInvariant)] private static partial Regex BenchForm();
    [GeneratedRegex(@"\bNo\.?(?=[\s\d])\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex NoToken();
    [GeneratedRegex(@"\s*-\s*/\s*", RegexOptions.CultureInvariant)] private static partial Regex DashBeforeSlash();
    [GeneratedRegex(@"^(?<type>[A-Za-z][A-Za-z0-9.()\-]{0,11})\s+-\s+(?<name>[^/]+?)\s*/\s*0*(?<no>\d+)\s*/\s*(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex TypeDashNameSlash();
    [GeneratedRegex(@"^(?<type>[A-Za-z][A-Za-z0-9.()\-\s]*?)\s*[/\s]\s*0*(?<no>\d+)\s*/\s*(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex TypeNoYear();
    [GeneratedRegex(@"^(?<type>[A-Za-z][A-Za-z0-9.()\-\s]*?)\s+-\s+0*(?<no>\d+)\s*/\s*(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex TypeDashNoYear();
    [GeneratedRegex(@"^0*(?<no>\d+)\s*/\s*(?<year>\d{4})$", RegexOptions.CultureInvariant)] private static partial Regex NoYear();
    [GeneratedRegex(@"^0*(?<no>\d+)$", RegexOptions.CultureInvariant)] private static partial Regex NumberOnly();
    [GeneratedRegex(@"\d\s*-?\s*$|\bNo\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex LooksLikeNumberText();
    [GeneratedRegex(@"\b138\b.*\bN\.?I\.?A?\.?\b|\bN\.?I\.?\s*Act\b.*\b138\b|\b138\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Section138();
    [GeneratedRegex(@"\b(?:19|20)\d{2}\b", RegexOptions.CultureInvariant)] private static partial Regex FourDigitYear();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
