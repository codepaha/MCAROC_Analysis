using System.Text.RegularExpressions;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

public enum PartyMatchOutcome { Verified, Ambiguous, Rejected }

/// <param name="Rule">Which rule decided: <c>exact</c>, <c>exact-former-name</c>, <c>legal-form-differs</c>, <c>extra-words</c>, <c>no-match</c>.</param>
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
        var party = Canonical(returnedParty);
        if (party.Length == 0) return new(PartyMatchOutcome.Rejected, "no-match", returnedParty, null);

        PartyMatchResult? ambiguous = null;
        foreach (var (name, former) in subjectNames.Select(n => (n, false)).Concat((formerNames ?? []).Select(n => (n, true))))
        {
            var candidate = Canonical(name);
            if (candidate.Length == 0) continue;
            if (party == candidate) return new(PartyMatchOutcome.Verified, former ? "exact-former-name" : "exact", returnedParty, name);
            if (StripLegalForm(party) == StripLegalForm(candidate) && StripLegalForm(candidate).Length > 0)
                ambiguous ??= new(PartyMatchOutcome.Ambiguous, "legal-form-differs", returnedParty, name);
        }
        if (ambiguous is not null) return ambiguous;

        var contains = subjectNames.Concat(formerNames ?? []).Select(Canonical).Any(c => c.Length > 0 && (" " + party + " ").Contains(" " + c + " "));
        return new(PartyMatchOutcome.Rejected, contains ? "extra-words" : "no-match", returnedParty, null);
    }

    /// <summary>The comparison form of a party or company name. Public so callers and tests can see exactly what is compared.</summary>
    public static string Canonical(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var t = name.ToUpperInvariant();
        t = RepresentationTail().Replace(t, " ");
        t = Others().Replace(t, " ");
        t = StatusNote().Replace(t, " ");
        t = MessrsPrefix().Replace(t, " ");
        t = t.Replace("&", " AND ");
        t = NonAlnum().Replace(t, " ");
        var words = t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalise).ToList();
        return string.Join(' ', words);
    }

    private static string Normalise(string w) => w switch { "LTD" => "LIMITED", "PVT" => "PRIVATE", _ => w };

    private static string StripLegalForm(string canonical) =>
        string.Join(' ', canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !LegalFormWords.Contains(w)));

    // Only recognised representation phrases are cut: "through its ...", "represented by ...", "rep. by ...", "its director/partner/...". A bare "through" is never
    // enough, because a different party can contain the word ("ALPHA LIMITED THROUGH INDUSTRIAL SERVICES PRIVATE LIMITED" is not ALPHA LIMITED).
    [GeneratedRegex(@"\b(?:(?:THROUGH|THR\.?|THRU)\s+ITS\b|REPRESENTED\s+BY\b|REP\.?\s+BY\b|REPD\.?\s+BY\b|ITS\s+(?:MANAGING\s+DIRECTOR|DIRECTOR|AUTHORI[SZ]ED|PROPRIETOR|PARTNER)\b).*$", RegexOptions.CultureInvariant)]
    private static partial Regex RepresentationTail();

    // "& Anr / & Ors / and others" is a suffix of the cause title only: anchored to the end, so "ALPHA AND ANOTHER INDUSTRIES LIMITED" stays a different party.
    [GeneratedRegex(@"[\s,]*(?:&|\bAND\b)\s*(?:ANR|ANOTHER|ORS|OTHERS)\b[\s,.;)]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Others();

    [GeneratedRegex(@"\(\s*(?:IN|UNDER)\s+(?:LIQUIDATION|CIRP|CIR\s+PROCESS|RESOLUTION|INSOLVENCY)[^)]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex StatusNote();

    [GeneratedRegex(@"\bM\s*[/.]\s*S\b\.?", RegexOptions.CultureInvariant)]
    private static partial Regex MessrsPrefix();

    [GeneratedRegex(@"[^A-Z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonAlnum();
}
