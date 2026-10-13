namespace MCAROC_Analysis.Data.Entities;

/// <summary>Spellings of a company name in another script (and written-out forms of an acronym in it), generated once and re-used by every request and refresh for that name (#489).
/// <see cref="SpellingsJson"/> holds only the spellings that passed the mechanical back-check; <see cref="RejectedJson"/> keeps the ones that did not,
/// for audit. A new prompt version generates afresh.</summary>
public class LitigationNameSpelling
{
    public long LitigationNameSpellingId { get; set; }

    /// <summary>The name compared: upper case, single spaces.</summary>
    public string NameKey { get; set; } = string.Empty;

    /// <summary>ISO 639-1 code of the spellings ("hi").</summary>
    public string Language { get; set; } = string.Empty;

    public string PromptVersion { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;

    /// <summary>JSON array of the accepted spellings.</summary>
    public string SpellingsJson { get; set; } = "[]";

    /// <summary>JSON array of {acronym, fullForm} whose initials spell the acronym (HDFC = Housing Development Finance Corporation).</summary>
    public string? ExpansionsJson { get; set; }

    /// <summary>JSON array of {spelling, reason} the back-check refused.</summary>
    public string? RejectedJson { get; set; }

    public DateTime CreatedUtc { get; set; }
}
