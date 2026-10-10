using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Normalisation;

/// <summary>Resolves the many spellings of a case type to one canonical short code: <c>Original Application-</c> -> <c>OA</c>,
/// <c>AP - ARBITRATION PETITION</c> -> <c>AP</c>, <c>CMP (Civil Misc Petition)</c> -> <c>CMP</c>, <c>consumer case(CC)</c> -> <c>CC</c>,
/// <c>O.S.</c> -> <c>OS</c>, <c>W.P.(C)</c> -> <c>WP(C)</c>. Returns <b>null for anything it does not recognise</b>: an unknown type is shown as
/// filed and marked, never guessed (plan section 3.2). The dictionary is seeded from the types seen in the sample data and grows from
/// data, with a test for each addition.</summary>
public static partial class CaseTypeCodes
{
    private static readonly Dictionary<string, string> LongNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["original application"] = "OA", ["securitization application"] = "SA", ["securitisation application"] = "SA",
        ["transfer application"] = "TA", ["transfer securitization application"] = "TSA", ["review application"] = "RA",
        ["misc application"] = "MA", ["miscellaneous application"] = "MA",
        ["arbitration petition"] = "AP", ["original misc. petition"] = "OMP", ["original misc petition"] = "OMP",
        ["civil misc. petition"] = "CMP", ["civil misc petition"] = "CMP", ["civil misc application"] = "CMA", ["civil misc. application"] = "CMA",
        ["civil suit"] = "CS", ["suit for recovery"] = "CS", ["original suit"] = "OS", ["original petition"] = "OP",
        ["writ petition"] = "WP", ["civil writ"] = "CW",
        ["execution"] = "EX", ["execution petition"] = "EP", ["execution petitions"] = "EP", ["exceution petition"] = "EP",
        ["consumer case"] = "CC", ["complaint cases"] = "CC", ["summons private cases ss"] = "SS", ["summons cases ss"] = "SS",
        ["income tax appeal"] = "ITA", ["transfer petition(companies act)"] = "TP(CA)",
    };

    /// <summary>Every code the resolver will confirm. An abbreviation-shaped string that is not here (<c>ZZZ</c>, <c>APPEAL</c>) stays unknown, so a
    /// guessed type is never presented as a confirmed one. Seeded from the two sample files plus the common court types; grows with a test per addition.</summary>
    private static readonly HashSet<string> KnownCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Supreme Court
        "SLP(C)", "SLP(CRL)", "SLP", "CA", "CRA", "WP(C)", "WP(CRL)", "TC(C)", "TC(CRL)", "TP(C)", "TP(CRL)", "RP(C)", "RP(CRL)", "CONMT.PET.(C)", "SA(C)",
        // High courts and district courts
        "WP", "WPC", "CWP", "CW", "CRLRC", "CRM", "CRM-M", "CRA-S", "CRA-D", "CRR", "CRP", "CRLP", "CRLA", "CRL.A", "CRLMC", "RFA", "RSA", "FAO", "FA", "LPA", "MFA", "SAO",
        "CM", "CMA", "CMP", "COMAS", "COMS", "COMA", "OS", "CS", "EX", "EXE", "EP", "OP", "OMP", "AP", "ARB", "ITA", "CEA", "STA", "TA", "TSA", "TP", "TP(CA)", "SS", "CC",
        "NDPS", "NACT", "SC", "CRL", "MC", "MCA", "RCA", "RCC", "SCC", "SCS", "ST", "MVC", "LAC", "LAR", "ELP", "HMA", "OA", "RA", "MA", "SA", "IA", "RP", "FR", "PCR",
        // NCLT / NCLAT / DRT / consumer
        "CP", "CP(IB)", "CA(IB)", "IA(IB)", "IB", "CP(CAA)", "CA(CAA)", "TSA", "TA", "MA", "IA", "OA", "SA", "RC", "RP", "FA", "CC",
    };

    /// <summary>Long names that carry a suffix (<c>Interlocutory Application (I.B.C)</c>).</summary>
    private static readonly (string Prefix, string Code)[] LongPrefixes = [("interlocutory appli", "IA")];

    public static string? Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var t = TrailingJunk().Replace(Collapse(raw), "");               // "Original Application-" / "ARBITRATION PETITION."
        if (t.Length == 0) return null;

        if ((CodeThenName().Match(t) is { Success: true } a)) return Known(a.Groups["code"].Value);      // "AP - ARBITRATION PETITION"
        if ((CodeThenBracketName().Match(t) is { Success: true } b)) return Known(b.Groups["code"].Value); // "CMP (Civil Misc Petition)"
        if (t.Length > 8 && (NameThenBracketCode().Match(t) is { Success: true } c)) return Known(c.Groups["code"].Value); // "consumer case(CC)"

        if (LongNames.TryGetValue(t, out var known)) return known;
        foreach (var (prefix, code) in LongPrefixes)
            if (t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return code;

        // A bare abbreviation: O.S., OS, CRM-M, CWP, W.P.(C), SLP(Crl), Com.A.S. A lowercase word of four or more letters ("Appeal", "Suit") is
        // a name, not a code, and is left unknown.
        if (Abbreviation().IsMatch(t))
        {
            var firstToken = Regex.Match(t, @"[A-Za-z]+").Value;
            if (!(firstToken.Length >= 4 && firstToken.Any(char.IsLower))) return Known(t);
        }
        return null;
    }

    private static string? Known(string code) { var c = Clean(code); return KnownCodes.Contains(c) ? c : null; }
    private static string Clean(string code) => Regex.Replace(code, @"[.\s]", "").ToUpperInvariant();
    private static string Collapse(string s) => Whitespace().Replace(s.Trim(), " ");

    [GeneratedRegex(@"[\s\-.]+$", RegexOptions.CultureInvariant)] private static partial Regex TrailingJunk();
    [GeneratedRegex(@"^(?<code>[A-Za-z0-9().\-]{1,10})\s+-\s+\S.*$", RegexOptions.CultureInvariant)] private static partial Regex CodeThenName();
    [GeneratedRegex(@"^(?<code>[A-Za-z0-9.\-]{2,8})\s*\((?<name>.{6,})\)$", RegexOptions.CultureInvariant)] private static partial Regex CodeThenBracketName();
    [GeneratedRegex(@"(?<!\))\((?<code>[A-Z]{1,6})\)\s*$", RegexOptions.CultureInvariant)] private static partial Regex NameThenBracketCode();
    [GeneratedRegex(@"^[A-Za-z]{1,6}(?:\.[A-Za-z]{1,6})*\.?\s*(?:\([A-Za-z]{1,6}\))*(?:-[A-Za-z]{1,3})?$", RegexOptions.CultureInvariant)] private static partial Regex Abbreviation();
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)] private static partial Regex Whitespace();
}
