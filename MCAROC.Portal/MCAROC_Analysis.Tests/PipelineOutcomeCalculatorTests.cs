using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Pipeline;

namespace MCAROC_Analysis.Tests;

public class PipelineOutcomeCalculatorTests
{
    private static readonly IReadOnlyList<PipelineStage> AllStages =
    [
        PipelineStage.Resolve, PipelineStage.Unlock, PipelineStage.Refresh, PipelineStage.Fetch,
        PipelineStage.Ingest, PipelineStage.Analysis, PipelineStage.CalcAssurance, PipelineStage.Dossier,
        PipelineStage.Filings, PipelineStage.Litigation, PipelineStage.LitigationAnalysis
    ];

    private static Dictionary<PipelineStage, PipelineStageStateKind> AllCoreSucceeded(
        PipelineStageStateKind enrichment = PipelineStageStateKind.NotStarted) =>
        AllStages.ToDictionary(s => s,
            s => PipelineOutcomeCalculator.CoreStages.Contains(s) ? PipelineStageStateKind.Succeeded : enrichment);

    private static readonly Dictionary<PipelineStage, PipelineStageSkipKind?> NoSkips =
        AllStages.ToDictionary(s => s, _ => (PipelineStageSkipKind?)null);

    [Fact]
    public void Cancelled_AlwaysWinsOverEverythingElse()
    {
        var states = AllCoreSucceeded(PipelineStageStateKind.NeedsAttention); // worst-case stage states too
        Assert.Equal(PipelineOutcome.Cancelled, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: true));
    }

    [Fact]
    public void AnyNeedsAttention_WinsOverCoreReadyAndComplete()
    {
        var states = AllCoreSucceeded();
        states[PipelineStage.Litigation] = PipelineStageStateKind.NeedsAttention;
        Assert.Equal(PipelineOutcome.NeedsAttention, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void CoreDoneButEnrichmentStillRunning_IsCoreReadyNotComplete()
    {
        var states = AllCoreSucceeded(PipelineStageStateKind.Running);
        Assert.Equal(PipelineOutcome.CoreReady, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void CoreIncomplete_IsInProgress_RegardlessOfEnrichmentState()
    {
        var states = AllCoreSucceeded();
        states[PipelineStage.Analysis] = PipelineStageStateKind.Running; // one core stage not yet done
        Assert.Equal(PipelineOutcome.InProgress, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void CoreIncomplete_RetryScheduled_IsStillInProgressNotNeedsAttention()
    {
        var states = AllCoreSucceeded();
        states[PipelineStage.Fetch] = PipelineStageStateKind.RetryScheduled;
        Assert.Equal(PipelineOutcome.InProgress, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void EverythingTerminalNoWarnings_IsComplete()
    {
        var states = AllCoreSucceeded(PipelineStageStateKind.Succeeded);
        Assert.Equal(PipelineOutcome.Complete, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void EverythingTerminalWithNeutralSkip_IsStillComplete()
    {
        // A manual-upload request: Fetch/Refresh/Unlock are Skipped(Neutral), not run at all — this must
        // still read as a clean Complete, not CompleteWithWarnings.
        var states = AllCoreSucceeded(PipelineStageStateKind.Skipped);
        var skips = NoSkips.ToDictionary(kv => kv.Key, kv => (PipelineStageSkipKind?)PipelineStageSkipKind.Neutral);
        Assert.Equal(PipelineOutcome.Complete, PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false));
    }

    [Fact]
    public void EverythingTerminalWithOneSucceededWithWarnings_IsCompleteWithWarnings()
    {
        var states = AllCoreSucceeded(PipelineStageStateKind.Succeeded);
        states[PipelineStage.Filings] = PipelineStageStateKind.SucceededWithWarnings;
        Assert.Equal(PipelineOutcome.CompleteWithWarnings, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void EverythingTerminalWithOneWarningKindSkip_IsCompleteWithWarnings()
    {
        // An enrichment stage (Litigation, not a core stage) skipped with a Warning kind — unaffected by the
        // core-completion gating below, still folds into CompleteWithWarnings as before.
        var states = AllCoreSucceeded(PipelineStageStateKind.Succeeded);
        states[PipelineStage.Litigation] = PipelineStageStateKind.Skipped;
        var skips = NoSkips.ToDictionary(kv => kv.Key, kv => kv.Key == PipelineStage.Litigation
            ? (PipelineStageSkipKind?)PipelineStageSkipKind.Warning
            : null);
        Assert.Equal(PipelineOutcome.CompleteWithWarnings, PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false));
    }

    [Fact]
    public void IsCoreReady_FalseUntilEveryCoreStageSucceeds()
    {
        var states = AllCoreSucceeded();
        Assert.True(PipelineOutcomeCalculator.IsCoreReady(states, NoSkips));

        states[PipelineStage.Dossier] = PipelineStageStateKind.Running;
        Assert.False(PipelineOutcomeCalculator.IsCoreReady(states, NoSkips));
    }

    [Fact]
    public void ManualUploadRequest_SkippedUnlockRefreshFetch_StillReachesComplete()
    {
        // Regression for a real P1: a manual-upload request has Unlock/Refresh/Fetch Skipped(MANUAL_SOURCE)
        // forever, not Succeeded — core completion must accept a legitimately-Skipped(Neutral) core stage as
        // done, or this pipeline sits in InProgress permanently even after Ingest/Analysis/CalcAssurance/
        // Dossier all genuinely succeed.
        var states = AllStages.ToDictionary(s => s, _ => PipelineStageStateKind.Succeeded);
        states[PipelineStage.Unlock] = PipelineStageStateKind.Skipped;
        states[PipelineStage.Refresh] = PipelineStageStateKind.Skipped;
        states[PipelineStage.Fetch] = PipelineStageStateKind.Skipped;
        var skips = NoSkips.ToDictionary(kv => kv.Key, kv =>
            states[kv.Key] == PipelineStageStateKind.Skipped ? (PipelineStageSkipKind?)PipelineStageSkipKind.Neutral : null);

        Assert.True(PipelineOutcomeCalculator.IsCoreReady(states, skips));
        Assert.Equal(PipelineOutcome.Complete, PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false));
    }

    [Fact]
    public void CoreStageSkippedWithWarningKind_DoesNotReachCoreReadyOrComplete()
    {
        // Regression for a real P1: a core stage's Skipped state must be gated on its SkipKind, not accepted
        // unconditionally — nothing in the design ever puts a core stage in Skipped(Warning); if one is ever
        // observed, the pipeline must not silently treat it as done.
        var states = AllStages.ToDictionary(s => s, _ => PipelineStageStateKind.Succeeded);
        states[PipelineStage.Fetch] = PipelineStageStateKind.Skipped;
        var skips = NoSkips.ToDictionary(kv => kv.Key, kv =>
            kv.Key == PipelineStage.Fetch ? (PipelineStageSkipKind?)PipelineStageSkipKind.Warning : null);

        Assert.False(PipelineOutcomeCalculator.IsCoreReady(states, skips));
        Assert.Equal(PipelineOutcome.InProgress, PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false));
    }

    [Fact]
    public void CoreStageSkippedWithNoRecordedKind_DoesNotReachCoreReadyOrComplete()
    {
        // Same as above but for a Skipped core stage with no SkipKind recorded at all (null) - treated the
        // same as Warning: fail closed, never assume it was the neutral/sanctioned case.
        var states = AllStages.ToDictionary(s => s, _ => PipelineStageStateKind.Succeeded);
        states[PipelineStage.Refresh] = PipelineStageStateKind.Skipped;
        // NoSkips already maps every stage (including Refresh) to null.

        Assert.False(PipelineOutcomeCalculator.IsCoreReady(states, NoSkips));
        Assert.Equal(PipelineOutcome.InProgress, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    [Fact]
    public void PerStageCancelledCoreStage_NeverSatisfiesCoreCompletion()
    {
        // A per-stage Cancelled state (distinct from the whole run being cancelled) means that stage was
        // aborted, not completed or legitimately skipped — it must never let coreDone become true.
        var states = AllCoreSucceeded();
        states[PipelineStage.Analysis] = PipelineStageStateKind.Cancelled;

        Assert.False(PipelineOutcomeCalculator.IsCoreReady(states, NoSkips));
        Assert.Equal(PipelineOutcome.InProgress, PipelineOutcomeCalculator.Aggregate(states, NoSkips, cancelled: false));
    }

    // ── Randomized invariant checks (docs/pipeline-automation-plan.md §8: "a generated-combination test
    // asserting the function is total ... and monotone [a NeedsAttention stage] can never improve the
    // outcome"). Fixed seed: a failure here must reproduce identically on every run. ──

    private static readonly PipelineStageStateKind[] AllStateKinds = Enum.GetValues<PipelineStageStateKind>();

    [Fact]
    public void Randomized_Totality_EveryCombinationYieldsExactlyOneConsistentOutcome()
    {
        var rng = new Random(20260262);
        for (var trial = 0; trial < 500; trial++)
        {
            var states = AllStages.ToDictionary(s => s, _ => AllStateKinds[rng.Next(AllStateKinds.Length)]);
            // Three-way random skip kind (Neutral / Warning / null-i.e.-unrecorded) so the totality check
            // also exercises the "missing kind fails closed" path, not just the two named kinds.
            var skips = AllStages.ToDictionary(s => s, s =>
            {
                if (states[s] != PipelineStageStateKind.Skipped) return (PipelineStageSkipKind?)null;
                return rng.Next(3) switch
                {
                    0 => PipelineStageSkipKind.Neutral,
                    1 => PipelineStageSkipKind.Warning,
                    _ => null
                };
            });
            var cancelled = rng.Next(4) == 0;

            var outcome = PipelineOutcomeCalculator.Aggregate(states, skips, cancelled);

            // Every sample must land in exactly one outcome consistent with the precedence rules — checked
            // as independent invariants, not by re-deriving the same code path.
            if (cancelled) { Assert.Equal(PipelineOutcome.Cancelled, outcome); continue; }
            if (states.Values.Any(v => v == PipelineStageStateKind.NeedsAttention))
            { Assert.Equal(PipelineOutcome.NeedsAttention, outcome); continue; }

            var coreDone = PipelineOutcomeCalculator.IsCoreReady(states, skips);
            if (!coreDone) { Assert.Equal(PipelineOutcome.InProgress, outcome); continue; }

            var anyNonTerminal = states.Values.Any(v => v is PipelineStageStateKind.NotStarted or
                PipelineStageStateKind.Waiting or PipelineStageStateKind.Running or PipelineStageStateKind.RetryScheduled);
            if (anyNonTerminal) { Assert.Equal(PipelineOutcome.CoreReady, outcome); continue; }

            Assert.True(outcome is PipelineOutcome.Complete or PipelineOutcome.CompleteWithWarnings);
        }
    }

    [Fact]
    public void Randomized_Monotonicity_ForcingNeedsAttentionNeverImprovesTheOutcome()
    {
        var rng = new Random(984150);
        for (var trial = 0; trial < 300; trial++)
        {
            var states = AllStages.ToDictionary(s => s, _ => AllStateKinds[rng.Next(AllStateKinds.Length)]);
            var skips = AllStages.ToDictionary(s => s, _ => (PipelineStageSkipKind?)null);

            var before = PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false);

            var target = AllStages[rng.Next(AllStages.Count)];
            states[target] = PipelineStageStateKind.NeedsAttention;
            var after = PipelineOutcomeCalculator.Aggregate(states, skips, cancelled: false);

            // NeedsAttention is the worst outcome short of a cancelled run, and this trial never cancels —
            // forcing one stage into it must always win, never leave a "better" outcome in place.
            Assert.Equal(PipelineOutcome.NeedsAttention, after);
            _ = before; // kept for readability of intent; the assertion on `after` is what matters here.
        }
    }
}
