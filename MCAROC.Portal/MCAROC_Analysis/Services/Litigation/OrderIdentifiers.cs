using System.Text;
using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationData;

public enum OrderIdentifierType { Cin, Llpin, Pan, Gstin, Tan, Din }

/// <summary>What an identifier named in an order says about whose case it is.</summary>
public enum IdentifierMatch
{
    /// <summary>It is the company's own CIN, LLPIN, PAN or a GSTIN of its PAN: the order is about this company.</summary>
    Company,
    /// <summary>It is the DIN of one of the company's directors.</summary>
    Director,
    /// <summary>It belongs to someone else: another entity or person named in the order.</summary>
    Other
}

/// <summary>One identifier exactly as printed in an order, the page it is on, and the words around it so it can be checked at a glance.</summary>
public sealed record OrderIdentifier(OrderIdentifierType Type, string Value, int Page, string Context);

/// <summary>The identifiers that identify the company in a case: its CIN/LLPIN, its PAN, its GSTINs and its directors' DINs.</summary>
public sealed record CompanyIdentity(string? Cin, string? Llpin, string? Pan, IReadOnlyCollection<string> Gstins, IReadOnlyCollection<string> DirectorDins)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Cin) && string.IsNullOrWhiteSpace(Llpin) && string.IsNullOrWhiteSpace(Pan) && Gstins.Count == 0 && DirectorDins.Count == 0;
}

/// <summary>How well a case is tied to the company by identifiers printed in its orders.</summary>
public enum IdentityEvidenceStatus
{
    /// <summary>An order names the company's own CIN, LLPIN, PAN or GSTIN.</summary>
    Confirmed,
    /// <summary>No company identifier, but an order names a director's DIN.</summary>
    DirectorOnly,
    /// <summary>Orders were read and name no identifier of the company: the case rests on the name match alone.</summary>
    NameOnly,
    /// <summary>No order text is available to read.</summary>
    NoOrderText
}

public sealed record MatchedOrderIdentifier(
    OrderIdentifier Identifier, IdentifierMatch Match, long OrderId, string? OrderDate, string? OrderType, long? DocumentId);

/// <summary>Reads government identifiers out of an order's text — exactly as printed, never inferred — and ties them to the company. The
/// formats are strict (a CIN is 21 characters with a real ownership code, a GSTIN carries a valid state code and a PAN-shaped middle);
/// the ones that look like any number or word (a DIN, a TAN, an LLPIN) count only where the text labels them. Personal identifiers
/// (Aadhaar, mobile and bank numbers) are deliberately not read: they play no part in telling whose case this is.</summary>
public static partial class OrderIdentifiers
{
    private static readonly HashSet<string> CinOwnership = ["PLC", "PTC", "FLC", "FTC", "GOI", "NPL", "ULL", "ULT", "OPC", "SGC", "GAP", "GAT"];
    private const string PanEntityLetters = "ABCFGHLJPT"; // the fourth character of a PAN: the kind of holder

    /// <summary>Every identifier in the text, with its page. The text is split on its "--- Page N ---" markers; a text without them is page 1.
    /// The same value twice on one page is one entry.</summary>
    public static IReadOnlyList<OrderIdentifier> Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<OrderIdentifier>();
        var seen = new HashSet<(OrderIdentifierType, string, int)>();
        foreach (var (page, pageText) in Pages(text))
        {
            var flat = Whitespace().Replace(pageText, " ");
            void Add(OrderIdentifierType type, Match m, string value)
            {
                if (!seen.Add((type, value, page))) return;
                var from = Math.Max(0, m.Index - 70);
                var to = Math.Min(flat.Length, m.Index + m.Length + 70);
                found.Add(new OrderIdentifier(type, value, page, (from > 0 ? "…" : "") + flat[from..to].Trim() + (to < flat.Length ? "…" : "")));
            }
            string Before(Match m) => flat[Math.Max(0, m.Index - 45)..m.Index];

            foreach (Match m in CinPattern().Matches(flat))
                if (CinOwnership.Contains(m.Groups["own"].Value)) Add(OrderIdentifierType.Cin, m, m.Value);
            foreach (Match m in GstinPattern().Matches(flat))
                if (int.Parse(m.Value[..2]) is >= 1 and <= 38 && PanEntityLetters.Contains(m.Value[5])) Add(OrderIdentifierType.Gstin, m, m.Value);
            foreach (Match m in PanPattern().Matches(flat))
                if (PanEntityLetters.Contains(m.Value[3])) Add(OrderIdentifierType.Pan, m, m.Value);
            foreach (Match m in TanPattern().Matches(flat))
                if (TanLabel().IsMatch(Before(m))) Add(OrderIdentifierType.Tan, m, m.Value);
            foreach (Match m in DinPattern().Matches(flat))
                if (DinLabel().IsMatch(Before(m))) Add(OrderIdentifierType.Din, m, m.Value);
            foreach (Match m in LlpinPattern().Matches(flat))
                if (LlpinLabel().IsMatch(Before(m))) Add(OrderIdentifierType.Llpin, m, m.Value);
        }
        return found;
    }

    /// <summary>Whose the identifier is, against the company's own identifiers. A GSTIN counts as the company's when it is one of its GSTINs
    /// or carries its PAN (a GSTIN is state code + PAN + registration number).</summary>
    public static IdentifierMatch Classify(OrderIdentifier id, CompanyIdentity company)
    {
        static string N(string? v) => (v ?? "").Replace(" ", "").Replace("-", "").ToUpperInvariant();
        var value = N(id.Value);
        var pan = N(company.Pan);
        return id.Type switch
        {
            OrderIdentifierType.Cin when value.Length > 0 && value == N(company.Cin) => IdentifierMatch.Company,
            OrderIdentifierType.Llpin when value.Length > 0 && value == N(company.Llpin) => IdentifierMatch.Company,
            OrderIdentifierType.Pan when pan.Length > 0 && value == pan => IdentifierMatch.Company,
            OrderIdentifierType.Gstin when company.Gstins.Any(g => N(g) == value) || (pan.Length > 0 && value.Length == 15 && value[2..12] == pan) => IdentifierMatch.Company,
            OrderIdentifierType.Din when company.DirectorDins.Any(d => N(d).TrimStart('0') == value.TrimStart('0')) => IdentifierMatch.Director,
            _ => IdentifierMatch.Other
        };
    }

    /// <summary>The standing of the case's identity from the identifiers its orders print.</summary>
    public static IdentityEvidenceStatus StatusOf(IReadOnlyCollection<MatchedOrderIdentifier> matched, int ordersWithText) =>
        matched.Any(m => m.Match == IdentifierMatch.Company) ? IdentityEvidenceStatus.Confirmed
        : matched.Any(m => m.Match == IdentifierMatch.Director) ? IdentityEvidenceStatus.DirectorOnly
        : ordersWithText > 0 ? IdentityEvidenceStatus.NameOnly
        : IdentityEvidenceStatus.NoOrderText;

    /// <summary>The identifier as shown on screen. A PAN keeps its first five characters and its check letter ("ABCDE****F"); the corporate
    /// identifiers (CIN, LLPIN, GSTIN, TAN, DIN) are public registry values and stay whole.</summary>
    public static string Mask(OrderIdentifierType type, string value)
    {
        var v = value.Trim().ToUpperInvariant();
        return type == OrderIdentifierType.Pan && v.Length == 10 ? $"{v[..5]}****{v[9]}" : value;
    }

    public static string Label(OrderIdentifierType type) => type switch
    {
        OrderIdentifierType.Cin => "CIN",
        OrderIdentifierType.Llpin => "LLPIN",
        OrderIdentifierType.Pan => "PAN",
        OrderIdentifierType.Gstin => "GSTIN",
        OrderIdentifierType.Tan => "TAN",
        _ => "DIN"
    };

    private static IEnumerable<(int Page, string Text)> Pages(string text)
    {
        var markers = PageMarker().Matches(text);
        if (markers.Count == 0) { yield return (1, text); yield break; }
        var page = 1;
        var pos = 0;
        foreach (Match m in markers)
        {
            if (m.Index > pos) yield return (page, text[pos..m.Index]);
            page = int.Parse(m.Groups["n"].Value);
            pos = m.Index + m.Length;
        }
        if (pos < text.Length) yield return (page, text[pos..]);
    }

    [GeneratedRegex(@"^--- Page (?<n>\d+) \((?:native|OCR)\) ---\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex PageMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[LU]\d{5}[A-Z]{2}\d{4}(?<own>[A-Z]{3})\d{6}(?![A-Za-z0-9])")]
    private static partial Regex CinPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z](?![A-Za-z0-9])")]
    private static partial Regex GstinPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{5}\d{4}[A-Z](?![A-Za-z0-9])")]
    private static partial Regex PanPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{4}\d{5}[A-Z](?![A-Za-z0-9])")]
    private static partial Regex TanPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])\d{8}(?![A-Za-z0-9])")]
    private static partial Regex DinPattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{3}-\d{4}(?![A-Za-z0-9])")]
    private static partial Regex LlpinPattern();

    [GeneratedRegex(@"(?:\bTAN\b|tax deduction (?:and collection )?account)[^A-Za-z0-9]{0,25}(?:no\.?|number)?[^A-Za-z0-9]{0,15}$", RegexOptions.IgnoreCase)]
    private static partial Regex TanLabel();

    [GeneratedRegex(@"(?:\bD\.?I\.?N\b|director identification number)[^A-Za-z0-9]{0,25}(?:no\.?|number)?[^A-Za-z0-9]{0,15}$", RegexOptions.IgnoreCase)]
    private static partial Regex DinLabel();

    [GeneratedRegex(@"(?:\bLLPIN\b|limited liability partnership identification (?:number|no\.?))[^A-Za-z0-9]{0,25}(?:no\.?|number)?[^A-Za-z0-9]{0,15}$", RegexOptions.IgnoreCase)]
    private static partial Regex LlpinLabel();
}
