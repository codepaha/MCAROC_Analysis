using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Pure aggregation of one run's stage states into a request-level <see cref="PipelineOutcome"/> —
/// first-match precedence over the rows below, per docs/pipeline-automation-plan.md §3.4. No I/O, no
/// mutable state: <see cref="PipelineReconcilerWorker"/> (not yet built) is the only caller, and it owns
/// persisting whatever this returns.</summary>
public static class PipelineOutcomeCalculator
{
    /// <summary>Terminal states — a stage in one of these will never be revisited on a future tick.</summary>
    private static bool IsTerminal(PipelineStageStateKind state) => state is
        PipelineStageStateKind.Succeeded or
        PipelineStageStateKind.SucceededWithWarnings or
        PipelineStageStateKind.Skipped or
        PipelineStageStateKind.Cancelled;

    /// <summary>The stages that must all be <see cref="PipelineStageStateKind.Succeeded"/> or <see
    /// cref="PipelineStageStateKind.SucceededWithWarnings"/> before <see cref="PipelineOutcome.CoreReady"/>
    /// can be reached — the core track, docs/pipeline-automation-plan.md §3.3's dependency graph.</summary>
    public static readonly IReadOnlyList<PipelineStage> CoreStages =
    [
        PipelineStage.Resolve, PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch,
        PipelineStage.Ingest, PipelineStage.Analysis, PipelineStage.CalcAssurance, PipelineStage.Dossier
    ];

    private static bool SucceededOrWarned(PipelineStageStateKind state) => state is
        PipelineStageStateKind.Succeeded or PipelineStageStateKind.SucceededWithWarnings;

    /// <param name="states">Every stage's current kind for this run — a stage never evaluated is expected
    /// to be present as <see cref="PipelineStageStateKind.NotStarted"/>, not absent, so this function stays
    /// total over a complete stage set.</param>
    /// <param name="skipKinds">The <see cref="PipelineStageSkipKind"/> for every stage currently <see
    /// cref="PipelineStageStateKind.Skipped"/> — absent or null for any other state.</param>
    /// <param name="cancelled">True once the run itself has been cancelled (a board action) — checked first,
    /// before any stage state, since a cancelled run's stage states are frozen and no longer meaningful.</param>
    public static PipelineOutcome Aggregate(
        IReadOnlyDictionary<PipelineStage, PipelineStageStateKind> states,
        IReadOnlyDictionary<PipelineStage, PipelineStageSkipKind?> skipKinds,
        bool cancelled)
    {
        // Row 1: cancelled short-circuits everything else.
        if (cancelled) return PipelineOutcome.Cancelled;

        // Row 2: any stage NeedsAttention.
        if (states.Values.Any(s => s == PipelineStageStateKind.NeedsAttention))
            return PipelineOutcome.NeedsAttention;

        var coreDone = CoreStages.All(stage => states.TryGetValue(stage, out var s) && SucceededOrWarned(s));
        var anyNonTerminal = states.Values.Any(s => !IsTerminal(s));

        // Row 3: core done, something else (an enrichment stage) still in flight -> CoreReady, not blocked.
        if (coreDone && anyNonTerminal) return PipelineOutcome.CoreReady;

        // Row 4: core not yet done (regardless of enrichment stage states).
        if (!coreDone) return PipelineOutcome.InProgress;

        // Rows 5/6: every stage terminal (implied by coreDone && !anyNonTerminal). Split on whether any
        // stage carries a warning — SucceededWithWarnings, or a Skipped stage whose SkipKind is Warning.
        var anyWarning = states.Any(kv =>
            kv.Value == PipelineStageStateKind.SucceededWithWarnings ||
            (kv.Value == PipelineStageStateKind.Skipped && skipKinds.TryGetValue(kv.Key, out var k) && k == PipelineStageSkipKind.Warning));

        return anyWarning ? PipelineOutcome.CompleteWithWarnings : PipelineOutcome.Complete;
    }

    /// <summary>Every <see cref="PipelineStage"/> that has ever reached <see
    /// cref="PipelineStageStateKind.Succeeded"/>/<see cref="PipelineStageStateKind.SucceededWithWarnings"/>
    /// for every stage in <see cref="CoreStages"/> marks the moment a dossier first became downloadable —
    /// callers persist this exactly once (the first tick this returns true) into <see
    /// cref="PipelineRun.CoreReadyUtc"/> and never clear it afterward, even if the outcome later becomes
    /// <see cref="PipelineOutcome.NeedsAttention"/> from an enrichment stage. This function only answers
    /// "is it true right now" — the once-only persistence is the reconciler's job, not this one's.</summary>
    public static bool IsCoreReady(IReadOnlyDictionary<PipelineStage, PipelineStageStateKind> states) =>
        CoreStages.All(stage => states.TryGetValue(stage, out var s) && SucceededOrWarned(s));
}
