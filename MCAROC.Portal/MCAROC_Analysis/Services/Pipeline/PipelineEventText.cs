using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>The <see cref="PipelineEvent.Action"/> values the coordinator writes.</summary>
public static class PipelineEventActions
{
    /// <summary>Prefix of a stage-state transition the reconciler observed, e.g. <c>Observed:Running</c>.</summary>
    public const string ObservedPrefix = "Observed:";
    public const string AutoStarted = "AutoStarted";
    public const string AutoStartDeferred = "AutoStartDeferred";

    /// <summary>Stage reason code while an automatically started job hasn't been observed yet.</summary>
    public const string AutoStartedCode = "AUTO_STARTED";

    public static string Observed(PipelineStageStateKind state) => ObservedPrefix + state;
}

/// <summary>Turns a pipeline event into one line of the request's step-by-step timeline. Reason codes stay stable
/// for code and tests; this is only their reader-facing wording.</summary>
public static class PipelineEventText
{
    private static readonly Dictionary<PipelineStage, string> StageLabels = new()
    {
        [PipelineStage.Resolve] = "Company identity",
        [PipelineStage.Unlock] = "Unlock",
        [PipelineStage.Refresh] = "Data refresh",
        [PipelineStage.Fetch] = "Fetch from the reference tool",
        [PipelineStage.Ingest] = "Ingestion",
        [PipelineStage.Analysis] = "AI analysis",
        [PipelineStage.CalcAssurance] = "Calculation checks",
        [PipelineStage.Dossier] = "Dossier",
        [PipelineStage.Filings] = "Filings",
        [PipelineStage.Litigation] = "Litigation search",
        [PipelineStage.LitigationAnalysis] = "Litigation analysis"
    };

    private static readonly Dictionary<PipelineStageStateKind, string> StateLabels = new()
    {
        [PipelineStageStateKind.NotStarted] = "not started",
        [PipelineStageStateKind.Waiting] = "waiting",
        [PipelineStageStateKind.Running] = "in progress",
        [PipelineStageStateKind.Succeeded] = "done",
        [PipelineStageStateKind.SucceededWithWarnings] = "done, with warnings",
        [PipelineStageStateKind.RetryScheduled] = "retry scheduled",
        [PipelineStageStateKind.NeedsAttention] = "needs attention",
        [PipelineStageStateKind.Skipped] = "skipped",
        [PipelineStageStateKind.Cancelled] = "cancelled"
    };

    private static readonly Dictionary<string, string> ReasonLabels = new(StringComparer.Ordinal)
    {
        ["WOULD_START"] = "ready to start",
        ["AUTO_STARTED"] = "started automatically",
        ["UNLOCK_APPROVAL_REQUIRED"] = "company is locked; approval needed to unlock (1 credit)",
        ["UNLOCK_EXPIRED"] = "unlock window expired; approval needed to unlock again (1 credit)",
        ["COST_CAP_REACHED"] = "today's automatic limit is used up",
        ["LITIGATION_RECENTLY_SEARCHED"] = "this company was searched by another request in the last 7 days",
        ["LITIGATION_SEARCH_IN_FLIGHT"] = "a search for this company is already running",
        ["LITIGATION_NOT_ELIGIBLE"] = "the request can't be searched automatically",
        ["AUTO_START_FAILED"] = "the automatic start failed; it will be tried again",
        ["POLICY_OFF"] = "not part of this pipeline's policy",
        ["INTEGRATION_NOT_CONFIGURED"] = "integration not configured",
        ["MANUAL_SOURCE"] = "manual upload",
        ["DATA_CURRENT"] = "data is current",
        ["INFERRED_FROM_EXPORT"] = "the export succeeded, so the company is unlocked"
    };

    public static string StageLabel(PipelineStage stage) => StageLabels.GetValueOrDefault(stage, stage.ToString());

    public static string ReasonLabel(string code) =>
        ReasonLabels.TryGetValue(code, out var label) ? label : Humanize(code);

    public static string Describe(PipelineEvent e)
    {
        var stage = StageLabel(e.Stage);
        var reason = string.IsNullOrWhiteSpace(e.ReasonCode) ? null : ReasonLabel(e.ReasonCode);
        string text;
        if (e.Action == PipelineEventActions.AutoStarted)
            text = $"{stage} started automatically";
        else if (e.Action == PipelineEventActions.AutoStartDeferred)
            text = $"{stage}: automatic start deferred";
        else if (e.Action.StartsWith(PipelineEventActions.ObservedPrefix, StringComparison.Ordinal)
                 && Enum.TryParse<PipelineStageStateKind>(e.Action[PipelineEventActions.ObservedPrefix.Length..], out var state))
            text = $"{stage}: {StateLabels.GetValueOrDefault(state, state.ToString())}";
        else
            text = $"{stage}: {e.Action}";
        return reason is null ? text : $"{text} — {reason}";
    }

    /// <summary><c>AWAITING_INGEST</c> → "awaiting ingest".</summary>
    private static string Humanize(string code) => code.Replace('_', ' ').ToLowerInvariant();
}
