namespace MCAROC_Analysis.Services.LitigationModule.PreLogin;

public enum PreLoginAttributionMode
{
    Suggest = 1,
    Apply = 2
}

public sealed class PreLoginAttributionOptions
{
    public const string SectionName = "PreLoginAttribution";

    /// <summary>
    /// Controls whether automatic case exclusion runs in Suggest mode (default) or Apply mode.
    /// In Suggest mode: cases are marked "Suggested exclusion" with party and reason, and logged for analyst review.
    /// In Apply mode: cases that are NotTheCompany are excluded automatically; Ambiguous cases stay in the report marked for review.
    /// </summary>
    public PreLoginAttributionMode Mode { get; set; } = PreLoginAttributionMode.Suggest;
}

