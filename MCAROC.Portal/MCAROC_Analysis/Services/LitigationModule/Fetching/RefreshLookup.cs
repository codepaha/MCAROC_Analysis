using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.LitigationModule.Normalisation;
using MCAROC_Analysis.Services.LitigationModule.Sources.Consumer;
using MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

namespace MCAROC_Analysis.Services.LitigationModule.Fetching;

/// <summary>What a status-refresh item carries: the lookup to send and the request whose party check (the link) the answer is for. A case with a gate-minted key is looked up
/// from its key and the stored lookup is ignored.</summary>
public sealed record RefreshPayload(long? RequestId, string Forum, int VenueId, string TypeCode, string No, int Year)
{
    public string ToJson() => JsonSerializer.Serialize(this);

    public static RefreshPayload? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<RefreshPayload>(json); }
        catch (JsonException) { return null; }
    }

    public static RefreshPayload From(PortalLookup lookup, long? requestId) =>
        new(requestId, lookup.Forum, lookup.VenueId, lookup.Parts.TypeCode!, lookup.Parts.No!, lookup.Parts.Year!.Value);

    public PortalLookup ToLookup()
    {
        var display = $"{TypeCode}/{No}/{Year}";
        // Lookup input only (plan 3.3): the year here may have been inferred from the vendor row. Identity is decided by what the portal returns, never by this.
        var parts = new CaseNumberParts(TypeCode, No, Year, YearSource.Number, CaseNumberCompleteness.Complete, display, null, [], null, null, [], display);
        return new PortalLookup(Forum == "CONSUMER" ? FetchHost.Consumer : FetchHost.Drt, Forum, VenueId, parts);
    }
}

/// <summary>Builds a portal lookup for a case that has no shared key yet (most vendor rows: their court is free text). Plan 3.3: parsed and inferred values may be used as
/// <b>lookup input</b>; they never become identity. The portal's answer is accepted only if it returns the same tribunal or commission, type, number and year
/// (<see cref="DrtObservationMapper.Verify"/>), and only then is a key minted, from what the portal returned.</summary>
public static class RefreshLookup
{
    public static PortalLookup? FromLegacy(LitigationCase c, out string reason)
    {
        reason = string.Empty;
        var parts = CaseNumberNormaliser.Parse(new CaseNumberInput(c.CaseNumber, c.CaseType, c.CaseYear, c.FilingDate, c.Cnr));
        if (parts.TypeCode is null || parts.No is null || parts.Year is null)
        {
            reason = "the case number has no recognised type, number and year to look up";
            return null;
        }
        if (parts.ConnectedMatters.Count > 0)
        {
            reason = "several case numbers are glued in one value";
            return null;
        }

        var court = c.Court;
        if (string.IsNullOrWhiteSpace(court)) { reason = "the court is not known"; return null; }

        if (DrtTribunalRegistry.Resolve(court) is { } tribunal)
        {
            var forum = tribunal.IsDrat ? "DRAT" : "DRT";
            if (DrtCaseTypeRegistry.GetCaseTypeId(parts.TypeCode, tribunal.IsDrat) is null)
            {
                reason = $"case type {parts.TypeCode} has no {forum} portal id";
                return null;
            }
            return new PortalLookup(FetchHost.Drt, forum, tribunal.Id, parts);
        }
        if (LooksLikeConsumerForum(court) && ConsumerCommissionRegistry.FindByCourtName(court, c.State) is { } commission)
            return new PortalLookup(FetchHost.Consumer, "CONSUMER", commission.CommissionId, parts);

        reason = "the court is not a DRT, DRAT or consumer commission covered by a portal";
        return null;
    }

    private static bool LooksLikeConsumerForum(string court)
    {
        var c = court.ToLowerInvariant();
        return c.Contains("consumer") || c.Contains("cdrc") || c.Contains("dcdrf") || c.Contains("ncdrc") || c.Contains("scdrc") || c.Contains("redressal");
    }
}
