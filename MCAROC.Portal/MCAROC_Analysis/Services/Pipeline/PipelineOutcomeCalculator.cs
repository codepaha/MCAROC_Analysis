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

    /// <summary>The stages that must all reach a *done* state (see <see cref="SatisfiesCoreCompletion"/>)
    /// before <see cref="PipelineOutcome.CoreReady"/> can be reached — the core track,
    /// docs/pipeline-automation-plan.md §3.3's dependency graph.</summary>
    public static readonly IReadOnlyList<PipelineStage> CoreStages =
    [
        PipelineStage.Resolve, PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch,
        PipelineStage.Ingest, PipelineStage.Analysis, PipelineStage.CalcAssurance, PipelineStage.Dossier
    ];

    /// <summary>A core stage counts as done when it succeeded (with or without warnings) OR when it was
    /// skipped for a documented, policy-sanctioned *neutral* reason — e.g. <c>Unlock</c>/<c>Refresh</c>/
    /// <c>Fetch</c> are <c>Skipped(MANUAL_SOURCE)</c> for a manual-upload request, per §3.3 — so that
    /// pipeline can still reach <see cref="PipelineOutcome.CoreReady"/>/<see cref="PipelineOutcome.Complete"/>
    /// instead of sitting in <see cref="PipelineOutcome.InProgress"/> forever because a stage that never
    /// runs for it never "succeeds" either.
    ///
    /// A <em>warning</em>-kind skip on a core stage does **not** count as done: nothing in the documented
    /// design ever puts a core stage in `Skipped(Warning)` — if one is ever observed, it means something
    /// unexpected happened to a stage the pipeline cannot treat as optional, and silently letting it satisfy
    /// core completion would smuggle a real problem past the reviewer. The same applies to a `Skipped` state
    /// with no recorded skip kind at all (a null <see cref="PipelineStageSkipKind"/>) — treated exactly like
    /// `Warning`, fail closed rather than assume it was fine.
    ///
    /// Deliberately narrower than <see cref="IsTerminal"/> in a second way too: a per-stage <see
    /// cref="PipelineStageStateKind.Cancelled"/> core stage must NOT count as done (the run was aborted, not
    /// completed) — <see cref="PipelineStageStateKind.NeedsAttention"/> never reaches this check at all,
    /// since row 2 in <see cref="Aggregate"/> already short-circuits on it first.</summary>
    private static bool SatisfiesCoreCompletion(PipelineStageStateKind state, PipelineStageSkipKind? skipKind) =>
        state is PipelineStageStateKind.Succeeded or PipelineStageStateKind.SucceededWithWarnings ||
        (state == PipelineStageStateKind.Skipped && skipKind == PipelineStageSkipKind.Neutral);

    private static bool CoreDone(
        IReadOnlyDictionary<PipelineStage, PipelineStageStateKind> states,
        IReadOnlyDictionary<PipelineStage, PipelineStageSkipKind?> skipKinds) =>
        CoreStages.All(stage =>
            states.TryGetValue(stage, out var s) &&
            SatisfiesCoreCompletion(s, skipKinds.TryGetValue(stage, out var k) ? k : null));

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

        var coreDone = CoreDone(states, skipKinds);
        var anyNonTerminal = states.Values.Any(s => !IsTerminal(s));

        // Row 3: core done, something else (an enrichment stage) still in flight -> CoreReady, not blocked.
        if (coreDone && anyNonTerminal) return PipelineOutcome.CoreReady;

        // Row 4: core not yet done (regardless of enrichment stage states).
        if (!coreDone) return PipelineOutcome.InProgress;

        // Rows 5/6: every stage terminal (implied by coreDone && !anyNonTerminal). Split on whether any
        // stage carries a warning — SucceededWithWarnings, or a Skipped stage whose SkipKind is Warning (or
        // absent — see SatisfiesCoreCompletion's doc comment on why a missing kind is treated as a warning,
        // not assumed neutral).
        var anyWarning = states.Any(kv =>
            kv.Value == PipelineStageStateKind.SucceededWithWarnings ||
            (kv.Value == PipelineStageStateKind.Skipped &&
                (!skipKinds.TryGetValue(kv.Key, out var k) || k != PipelineStageSkipKind.Neutral)));

        return anyWarning ? PipelineOutcome.CompleteWithWarnings : PipelineOutcome.Complete;
    }

    /// <summary>True once every stage in <see cref="CoreStages"/> satisfies <see
    /// cref="SatisfiesCoreCompletion"/> — marks the moment a dossier first became downloadable. Callers
    /// persist this exactly once (the first tick this returns true) into <see
    /// cref="PipelineRun.CoreReadyUtc"/> and never clear it afterward, even if the outcome later becomes
    /// <see cref="PipelineOutcome.NeedsAttention"/> from an enrichment stage. This function only answers
    /// "is it true right now" — the once-only persistence is the reconciler's job, not this one's.</summary>
    public static bool IsCoreReady(
        IReadOnlyDictionary<PipelineStage, PipelineStageStateKind> states,
        IReadOnlyDictionary<PipelineStage, PipelineStageSkipKind?> skipKinds) =>
        CoreDone(states, skipKinds);
}
