namespace MCAROC_Analysis.Services.LitigationModule.Normalisation;

/// <param name="Forum">The forum family (<c>DRT</c>, <c>CONSUMER</c>, <c>HC</c> ...), from the source's own court type.</param>
/// <param name="CourtKey">The court's <b>authoritative</b> id (tribunal id, commission id, known High Court code). Null whenever the court is only known from free text.</param>
/// <param name="Parts">The parsed case number.</param>
/// <param name="PortalReturned">For a portal-sourced row, the identity the portal sent back. It must equal what was looked up; null for rows that did not come from a portal.</param>
public sealed record CaseKeyInput(string? Forum, string? CourtKey, CaseNumberParts Parts, CaseNumberParts? PortalReturned = null, bool FromPortal = false);

/// <param name="Key">The shared case key <c>FORUM|COURT-KEY|TYPE|NO|YEAR</c>, or null when the row does not pass the gate.</param>
/// <param name="Reason">Why there is no key (empty when there is one).</param>
public sealed record CaseKeyResult(string? Key, string Reason);

/// <summary>The identity gate (plan section 3.3). <b>No parsed or inferred value becomes shared identity.</b> A row that fails stays a separate
/// observation: displayed and refreshable, but never linked, merged or deduplicated on its key. Existing CNR-first de-duplication is separate and unchanged.</summary>
public static class CaseKeyGate
{
    public static CaseKeyResult Evaluate(CaseKeyInput input)
    {
        var p = input.Parts;
        if (string.IsNullOrWhiteSpace(input.Forum)) return No("forum not known");
        if (string.IsNullOrWhiteSpace(input.CourtKey)) return No("court not resolved from an authoritative id");   // never from free-text court names
        if (p.TypeCode is null) return No("case type not in the dictionary");                                     // unknown types are never guessed
        if (p.No is null) return No("no case number");
        if (p.ConnectedMatters.Count > 0) return No("several numbers glued in one value");                        // each may be keyed on its own, never the glued whole
        if (p.Year is null) return No("no year");
        if (!YearIsKeyable(p)) return No("year rests on one inferred signal");                                      // filing date alone, or CNR alone, is not enough

        if (input.FromPortal)
        {
            var r = input.PortalReturned;
            if (r is null) return No("the portal returned no identity to compare");
            if (!YearIsKeyable(r)) return No("the portal's returned year rests on one inferred signal");              // the returned identity must pass the same year rule
            if (!SameIdentity(p, r)) return No("the identity the portal returned differs from the one looked up");
            p = r;                                                                                                  // minted from what the portal returned
        }
        return new CaseKeyResult($"{input.Forum.Trim().ToUpperInvariant()}|{input.CourtKey.Trim().ToUpperInvariant()}|{p.TypeCode}|{p.No}|{p.Year}", string.Empty);
    }

    private static bool YearIsKeyable(CaseNumberParts p) => p.YearSource is YearSource.Number or YearSource.Column or YearSource.InferredFilingAndCnr;

    private static bool SameIdentity(CaseNumberParts a, CaseNumberParts b) =>
        a.TypeCode is not null && a.TypeCode == b.TypeCode && a.No is not null && a.No == b.No && a.Year is not null && a.Year == b.Year && b.ConnectedMatters.Count == 0;

    private static CaseKeyResult No(string reason) => new(null, reason);
}
