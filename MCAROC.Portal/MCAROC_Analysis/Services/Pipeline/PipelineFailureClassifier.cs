namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>Plan §6.1's seven-way failure taxonomy — what kind of problem a stage's stable reason code
/// represents, which is what decides whether the coordinator may retry it on its own or must leave it for a
/// human. Deliberately not the same thing as <see cref="PipelineStageStateKind"/>: a stage can be
/// <c>NeedsAttention</c> for reasons ranging from "try again in a minute" to "a developer needs to look at
/// this."</summary>
public enum PipelineFailureClass
{
    /// <summary>Tool 5xx/timeout, rate limiting, a renderer crash, disk-full-then-freed — safe to retry with
    /// backoff; the next attempt might just work.</summary>
    Transient,

    /// <summary>A session/login loss — handled entirely inside the session layer (re-login and replay) and
    /// never reaches the coordinator as a stage-level failure unless the re-login itself fails, at which
    /// point it surfaces as <see cref="NeedsHumanConfig"/> instead. Kept as its own class for completeness
    /// with the plan's table, even though no reason code maps to it today.</summary>
    RecoverableAuth,

    /// <summary>A genuine data problem — identity mismatch, a workbook that won't parse, a calculation-
    /// assurance hold, an unrecoverable stall. Needs a human to look at the data; retrying changes nothing.</summary>
    NeedsHumanData,

    /// <summary>The company is locked or its unlock window expired — proceeds only on an explicit, single-use
    /// approval (plan §5.7), never automatically.</summary>
    ApprovalGated,

    /// <summary>The target name matches several plausible companies, or none — a human must select the
    /// correct one (or confirm none exists); auto-selecting would risk buying the wrong company's data.</summary>
    AmbiguousIdentity,

    /// <summary>Rejected credentials or a genuinely unconfigured integration — the circuit breaker opens (or
    /// would, if not already) and nothing about retrying fixes a config problem.</summary>
    NeedsHumanConfig,

    /// <summary>The vendor's contract changed underneath us, or an exception type nothing anticipated — worth
    /// one retry in case it was a fluke, but if it recurs a developer needs to look, not a reviewer.</summary>
    NeedsDeveloper
}

/// <summary>Pure lookup from a stage's stable <c>ReasonCode</c> (docs/pipeline-automation-plan.md §6.1's
/// list) to its failure class. An unrecognised code fails closed to <see cref="PipelineFailureClass.NeedsHumanData"/>
/// — never auto-retried — rather than guessing it is safe to retry.</summary>
public static class PipelineFailureClassifier
{
    private static readonly Dictionary<string, PipelineFailureClass> Map = new(StringComparer.Ordinal)
    {
        // Transient — the coordinator may retry these with backoff (plan §6.2).
        ["FETCH_FAILED"] = PipelineFailureClass.Transient,
        ["INGEST_FAILED"] = PipelineFailureClass.Transient,
        ["ANALYSIS_FAILED"] = PipelineFailureClass.Transient,
        ["FILINGS_FAILED"] = PipelineFailureClass.Transient,
        ["FILINGS_FETCH_FAILED"] = PipelineFailureClass.Transient,
        ["DOSSIER_RENDER_FAILED"] = PipelineFailureClass.Transient,
        ["REFRESH_TIMEOUT"] = PipelineFailureClass.Transient,
        ["MCA_MAINTENANCE"] = PipelineFailureClass.Transient,
        ["TOOL_UNAVAILABLE"] = PipelineFailureClass.Transient,
        ["LITIGATION_SEARCH_FAILED"] = PipelineFailureClass.Transient,
        ["LITIGATION_ANALYSIS_FAILED"] = PipelineFailureClass.Transient,
        ["LITIGATION_IMPORT_FAILED"] = PipelineFailureClass.Transient,

        // NeedsHuman-data — a real data problem, or a stall (never auto-restarted — plan §6.3 — an unfenced
        // worker might still be alive, and restarting it risks double execution).
        ["IDENTITY_MISMATCH"] = PipelineFailureClass.NeedsHumanData,
        ["IDENTITY_FALSE_ACCEPT"] = PipelineFailureClass.NeedsHumanData,
        ["REQUEST_ALREADY_IDENTIFIED"] = PipelineFailureClass.NeedsHumanData,
        ["IDENTIFIER_INVALID"] = PipelineFailureClass.NeedsHumanData,
        ["IDENTIFIER_NOT_IN_MASTER"] = PipelineFailureClass.NeedsHumanData,
        ["NAME_MISSING"] = PipelineFailureClass.NeedsHumanData,
        ["CALC_GATE_HOLD"] = PipelineFailureClass.NeedsHumanData,
        ["MANUAL_REVIEW_REQUIRED"] = PipelineFailureClass.NeedsHumanData,
        ["DUPLICATE_REQUEST"] = PipelineFailureClass.NeedsHumanData,
        ["STAGE_STALLED"] = PipelineFailureClass.NeedsHumanData,
        ["ORDER_DOWNLOAD_STALLED"] = PipelineFailureClass.NeedsHumanData,
        ["RETRIES_EXHAUSTED"] = PipelineFailureClass.NeedsHumanData,

        // Approval-gated.
        ["UNLOCK_APPROVAL_REQUIRED"] = PipelineFailureClass.ApprovalGated,
        ["UNLOCK_EXPIRED"] = PipelineFailureClass.ApprovalGated,

        // Ambiguous-identity.
        ["IDENTITY_AMBIGUOUS"] = PipelineFailureClass.AmbiguousIdentity,
        ["IDENTITY_NOT_FOUND"] = PipelineFailureClass.AmbiguousIdentity,

        // NeedsHuman-config.
        ["AUTH_REJECTED"] = PipelineFailureClass.NeedsHumanConfig,
        ["INTEGRATION_NOT_CONFIGURED"] = PipelineFailureClass.NeedsHumanConfig,
        ["LITIGATION_NOT_CONFIGURED"] = PipelineFailureClass.NeedsHumanConfig,

        // NeedsDeveloper.
        ["CONTRACT_CHANGED"] = PipelineFailureClass.NeedsDeveloper,
    };

    public static PipelineFailureClass Classify(string? reasonCode) =>
        reasonCode is not null && Map.TryGetValue(reasonCode, out var cls) ? cls : PipelineFailureClass.NeedsHumanData;

    /// <summary>Only <see cref="PipelineFailureClass.Transient"/> stages are ever retried by the coordinator
    /// on its own (plan §6.2) — every other class either needs a human decision or, for
    /// <see cref="PipelineFailureClass.NeedsDeveloper"/>, gets exactly one retry before that (handled by the
    /// caller's own attempt count, not by this classifier reclassifying itself after one try).</summary>
    public static bool IsAutoRetryable(string? reasonCode) => Classify(reasonCode) == PipelineFailureClass.Transient;
}
