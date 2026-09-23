namespace MCAROC_Analysis.Services.Pipeline;

/// <summary><c>Pipeline</c> configuration section (docs/pipeline-automation-plan.md §4.0). Read through
/// <c>IOptionsMonitor</c> at admit time, so a changed cap takes effect on the next admission without a
/// restart.</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Time zone whose calendar day is the daily-cap bucket (<c>DayKey</c>) — never server-local
    /// time or UTC.</summary>
    public string CapTimeZone { get; set; } = "Asia/Kolkata";

    /// <summary>A <c>Reserved</c> admission with no job/run linked to it for this long is treated as a crash
    /// between admission and the call, and released (after first checking the request for a job/run the
    /// admission may have produced — see <see cref="PaidCallAdmissionService"/>).</summary>
    public int ReservationTtlMinutes { get; set; } = 30;

    /// <summary>How often <see cref="PaidCallAdmissionSweepWorker"/> resolves outstanding reservations.</summary>
    public int AdmissionSweepMinutes { get; set; } = 2;

    public PipelineCapOptions Caps { get; set; } = new();
}

/// <summary>Daily caps on <c>Auto</c> admissions per kind. Manual admissions are counted against the same
/// counter but never blocked by it. The two real-spend caps default to 0 — inert until an operator sets a
/// positive number, this repo's fail-safe convention.</summary>
public sealed class PipelineCapOptions
{
    /// <summary>Rate-limit guard against BPR's unconfirmed limits, not a spend guard — the search itself is free.</summary>
    public int LitigationSearchPerDay { get; set; } = 50;
    public int LitigationAnalysisPerDay { get; set; } = 0;
    public int UnlockPerDay { get; set; } = 0;
}
