using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>
/// Display formatting, badges, and icon classes for <see cref="LitigationOrderOutcome"/>.
/// </summary>
public static class LitigationOrderOutcomeLabels
{
    public static string Format(LitigationOrderOutcome outcome) => outcome switch
    {
        LitigationOrderOutcome.FinePenalty => "Fine / Penalty",
        LitigationOrderOutcome.StayGranted => "Stay Granted",
        LitigationOrderOutcome.StayVacated => "Stay Vacated",
        LitigationOrderOutcome.PossessionOrder => "Possession Order",
        LitigationOrderOutcome.Injunction => "Injunction",
        LitigationOrderOutcome.Dismissal => "Dismissal",
        LitigationOrderOutcome.DisposedSettled => "Disposed / Settled",
        LitigationOrderOutcome.InterimRelief => "Interim Relief",
        LitigationOrderOutcome.AdjournedNoSubstantiveOrder => "Adjourned",
        _ => outcome.ToString()
    };

    public static string BadgeCss(LitigationOrderOutcome outcome) => outcome switch
    {
        LitigationOrderOutcome.FinePenalty => "bg-danger-subtle text-danger border border-danger-subtle",
        LitigationOrderOutcome.StayGranted => "bg-warning text-dark",
        LitigationOrderOutcome.StayVacated => "bg-info text-dark",
        LitigationOrderOutcome.PossessionOrder => "bg-primary text-white",
        LitigationOrderOutcome.Injunction => "bg-danger text-white",
        LitigationOrderOutcome.Dismissal => "bg-secondary text-white",
        LitigationOrderOutcome.DisposedSettled => "bg-success text-white",
        LitigationOrderOutcome.InterimRelief => "bg-info-subtle text-info-emphasis border border-info-subtle",
        LitigationOrderOutcome.AdjournedNoSubstantiveOrder => "bg-light text-muted border",
        _ => "bg-secondary text-white"
    };

    public static string IconCss(LitigationOrderOutcome outcome) => outcome switch
    {
        LitigationOrderOutcome.FinePenalty => "bi-currency-rupee",
        LitigationOrderOutcome.StayGranted => "bi-pause-circle",
        LitigationOrderOutcome.StayVacated => "bi-play-circle",
        LitigationOrderOutcome.PossessionOrder => "bi-box-seam",
        LitigationOrderOutcome.Injunction => "bi-slash-circle",
        LitigationOrderOutcome.Dismissal => "bi-x-circle",
        LitigationOrderOutcome.DisposedSettled => "bi-check-circle",
        LitigationOrderOutcome.InterimRelief => "bi-shield-check",
        LitigationOrderOutcome.AdjournedNoSubstantiveOrder => "bi-calendar-event",
        _ => "bi-tag"
    };
}
