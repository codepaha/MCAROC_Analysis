using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

public enum PartyMatchOutcome { Verified, Ambiguous, Rejected }

/// <param name="Rule">Which rule decided: <c>exact</c>, <c>exact-former-name</c>, <c>exact-representative</c> / <c>exact-former-name-representative</c> (an officer,
/// liquidator or resolution professional named as acting for the company), <c>legal-form-differs</c>, <c>extra-words</c>, <c>no-match</c>.</param>
public sealed record PartyMatchResult(PartyMatchOutcome Outcome, string Rule, string? MatchedParty, string? MatchedName);

/// <summary>Whole-name party verification (plan section 4.3): a returned party must <b>equal</b> one of the subject's names (current, former or registered alias) after
/// stripping boilerplate. Containing the name is never enough, so <c>KOTAK MAHINDRA BANK LIMITED EMPLOYEES COOPERATIVE SOCIETY</c> is not the bank. The old crawler's
/// "alias of 12 or more characters contained in the party" rule is deliberately not carried over.
/// <para>Boilerplate removed: <c>M/s</c>, punctuation, case, <c>&amp; Anr / Ors / Others</c>, and a representation tail (<c>through its director ...</c>, <c>rep. by ...</c>).
/// <c>Ltd</c> and <c>Limited</c> are the same word, as are <c>Pvt</c> and <c>Private</c>. A trailing <c>(in liquidation)</c>-style note is a state of the same party, not another party.</para></summary>
public static partial class WholeNamePartyMatcher
{
    private static readonly string[] LegalFormWords = ["LIMITED", "PRIVATE", "LLP", "PUBLIC", "COMPANY", "CO", "CORPORATION", "CORP", "INC"];

    /// <summary>The best result of matching one returned party against all the subject's names: Verified beats Ambiguous beats Rejected.</summary>
    public static PartyMatchResult Match(string? returnedParty, IEnumerable<string> subjectNames, IEnumerable<string>? formerNames = null)
    {
        // The vendor numbers the parties of a side ("2 3M INDIA LIMITED"), but a number can also begin a name ("20 MICRONS LIMITED"): the party is read
        // both as written and without a leading number, and either reading may match.
        var parties = new[] { returnedParty, returnedParty is null ? null : ListNumber().Replace(returnedParty.TrimStart(), "") }
            .Select(Canonical).Where(p => p.Length > 0).Distinct().ToList();
        if (parties.Count == 0) return new(PartyMatchOutcome.Rejected, "no-match", returnedParty, null);
        // A representation phrase after the name ("..., rep. by its official liquidator") is cut first, so an office named there is not read as acting before it.
        var throughRepresentative = RepresentativePrefix().IsMatch(RepresentationTail().Replace(Prepare(ListNumber().Replace(returnedParty!.TrimStart(), "")), " ").Trim());

        PartyMatchResult? ambiguous = null;
        foreach (var (name, former) in subjectNames.Select(n => (n, false)).Concat((formerNames ?? []).Select(n => (n, true))))
        {
            var candidate = Canonical(name);
            if (candidate.Length == 0) continue;
            foreach (var party in parties)
            {
                if (party == candidate)
                    return new(PartyMatchOutcome.Verified, (former ? "exact-former-name" : "exact") + (throughRepresentative ? "-representative" : ""), returnedParty, name);
                if (StripLegalForm(party) == StripLegalForm(candidate) && StripLegalForm(candidate).Length > 0)
                    ambiguous ??= new(PartyMatchOutcome.Ambiguous, "legal-form-differs", returnedParty, name);
            }
        }
        if (ambiguous is not null) return ambiguous;

        var contains = subjectNames.Concat(formerNames ?? []).Select(Canonical)
            .Any(c => c.Length > 0 && parties.Any(party => (" " + party + " ").Contains(" " + c + " ")));
        return new(PartyMatchOutcome.Rejected, contains ? "extra-words" : "no-match", returnedParty, null);
    }

    /// <summary>The comparison form of a party or company name. Public so callers and tests can see exactly what is compared.</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var t = Prepare(name);
        t = RepresentationTail().Replace(t, " ").Trim();
        t = RepresentativePrefix().Replace(t, " ");
        t = Others().Replace(t, " ");
        t = StatusNote().Replace(t, " ");
        t = MessrsPrefix().Replace(t, " ");
        t = t.Replace("&", " AND ");
        t = NonAlnum().Replace(t, " ");
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalise).ToList();
        return string.Join(' ', words);
    }

    private static string Normalise(string w) => w switch { "LTD" => "LIMITED", "PVT" => "PRIVATE", _ => w };

    /// <summary>Upper case without a leading "The" or an M/s written without its slash (or misread as "MIS") ("MS COASTAL ...",
    /// "MS.COASTAL ...") and a trailing record note ("(NOT APPLICABLE)", "(DEAD)"); "P LTD" is Private Limited. Seen in Coastal's 626 vendor cases (#489).</summary>
    private static string Prepare(string name)
    {
        var t = name.ToUpperInvariant().Trim();
        t = BareMessrs().Replace(t, "");
        t = RecordNote().Replace(t, " ");
        t = PrivateInitial().Replace(t, " PRIVATE LIMITED");
        return t.Trim();
    }

    // "1 ", "2. ", "3) " at the start: the vendor numbers the parties of a side.
    [GeneratedRegex(@"^\d{1,3}[.)]?\s+", RegexOptions.CultureInvariant)]
    private static partial Regex ListNumber();

    // "MS COASTAL", "MS. COASTAL", "MS.COASTAL": M/s without its slash, only at the start and only before another word.
    [GeneratedRegex(@"^(?:THE\s+)?(?:MS|MIS)\b\.?\s*(?=[A-Z0-9])|^THE\s+(?=[A-Z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex BareMessrs();

    // A note the record keeps after the name, not part of it.
    [GeneratedRegex(@"\(\s*(?:NOT\s+APPLICABLE|N\.?\s*A\.?|DEAD|DELETED|DECEASED|EXPIRED|SINCE\s+DECEASED|REMOVED)\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex RecordNote();

    // "P LTD", "P. LTD.": Private Limited.
    [GeneratedRegex(@"\sP\.?\s+(?:LTD\.?|LIMITED)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateInitial();

    // The company named through the office that acts for it: "THE CHAIRMAN, X", "MANAGING DIRECTOR OF X", "<NAME> THE LIQUIDATOR FOR X",
    // "RESOLUTION PROFESSIONAL OF X". What follows is the party; a person sued in their own name has no such office before the company's name.
    [GeneratedRegex(@"^(?:.{0,60}?\b)?(?:THE\s+)?(?:CHAIRMAN|CHAIRPERSON|MANAGING\s+DIRECTOR|GENERAL\s+MANAGER|CHIEF\s+EXECUTIVE\s+OFFICER|AUTHORI[SZ]ED\s+SIGNATORY|(?:OFFICIAL\s+)?LIQUIDATOR|(?:INTERIM\s+)?RESOLUTION\s+PROFESSIONAL|ADMINISTRATOR)\s*(?:,|\bOF\b|\bFOR\b|\bTO\b)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex RepresentativePrefix();

    private static string StripLegalForm(string canonical) =>
        string.Join(' ', canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !LegalFormWords.Contains(w)));

    // Only recognised representation phrases are cut: "through its ...", "represented by ...", "rep. by ...", "its director/partner/...". A bare "through" is never
    // enough, because a different party can contain the word ("ALPHA LIMITED THROUGH INDUSTRIAL SERVICES PRIVATE LIMITED" is not ALPHA LIMITED).
    [GeneratedRegex(@"\b(?:(?:THROUGH|THR\.?|THRU)\s+ITS\b|REPRESENTED\s+BY\b|REP\.?\s*BY\b|REPD\.?\s*BY\b|ITS\s+(?:MANAGING\s+DIRECTOR|DIRECTOR|AUTHORI[SZ]ED|PROPRIETOR|PARTNER)\b).*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepresentationTail();

    // "& Anr / & Ors / and others" is a suffix of the cause title only: anchored to the end, so "ALPHA AND ANOTHER INDUSTRIES LIMITED" stays a different party.
    [GeneratedRegex(@"[\s,]*(?:&|\bAND\b)\s*(?:\d{1,4}\s*)?(?:ANR|ANOTHER|ORS|OTHERS)\b[\s,.;)]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Others();

    [GeneratedRegex(@"\(\s*(?:IN|UNDER)\s+(?:LIQUIDATION|CIRP|CIR\s+PROCESS|RESOLUTION|INSOLVENCY)[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex StatusNote();

    [GeneratedRegex(@"\bM\s*[/.]\s*S\b\.?", RegexOptions.CultureInvariant)]
    private static partial Regex MessrsPrefix();

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();
}
