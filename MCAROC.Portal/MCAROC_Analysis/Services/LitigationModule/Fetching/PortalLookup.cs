using System.Globalization;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

/// <summary>Where and how to look a case up on a court portal: the host, the authoritative venue id (tribunal or commission) and the case number parts.</summary>
public sealed record PortalLookup(FetchHost Host, string Forum, int VenueId, CaseNumberParts Parts)
{
    public bool IsDrat => Forum == "DRAT" || DrtTribunalRegistry.IsDrat(VenueId);

    /// <summary>Builds the lookup from the case's shared key (<c>FORUM|COURT-KEY|TYPE|NO|YEAR</c>, minted only by <see cref="CaseKeyGate"/> from an authoritative court id), so a refresh
    /// never resolves a court from free text. A case without a key, or of a forum no portal covers, has no lookup: it cannot be refreshed and is reported, not guessed.</summary>
    public static PortalLookup? FromCaseKey(string? caseKey, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrWhiteSpace(caseKey)) { reason = "the case has no verified case key"; return null; }
        var p = caseKey.Split('|');
        if (p.Length != 5) { reason = "the case key is not in the expected form"; return null; }
        var forum = p[0];
        FetchHost? host = forum switch { "DRT" or "DRAT" => FetchHost.Drt, "CONSUMER" => FetchHost.Consumer, _ => null };
        if (host is null) { reason = $"no court portal covers the forum {forum}"; return null; }
        if (!int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var venue) || venue <= 0) { reason = "the court key is not a portal id"; return null; }
        if (!int.TryParse(p[4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)) { reason = "the case key has no year"; return null; }
        var display = $"{p[2]}/{p[3]}/{year}";
        var parts = new CaseNumberParts(p[2], p[3], year, YearSource.Number, CaseNumberCompleteness.Complete, display, null, [], null, null, [], display);
        return new PortalLookup(host.Value, forum, venue, parts);
    }
}
