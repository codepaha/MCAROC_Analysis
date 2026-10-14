namespace MCAROC_Analysis.Data.Entities;

/// <summary>A judgement of whether one party name, as court records write it, is one company (#489, decision a). Cached per company and party name, so a
/// refresh or another request for the same company pays nothing for a party already judged. A new prompt version judges afresh.</summary>
public class LitigationPartyVerdict
{
    public long LitigationPartyVerdictId { get; set; }

    /// <summary>The company: its CIN/LLPIN, or for a request without one a hash of its name.</summary>
    public string SubjectKey { get; set; } = string.Empty;

    /// <summary>The party name in the whole-name rule's comparison form.</summary>
    public string PartyKey { get; set; } = string.Empty;

    /// <summary>The party name as it was judged (one of the ways it is written).</summary>
    public string PartyName { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public LitigationPartyVerdictKind Verdict { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedUtc { get; set; }
}

public enum LitigationPartyVerdictKind { Same, Different, Unsure }
