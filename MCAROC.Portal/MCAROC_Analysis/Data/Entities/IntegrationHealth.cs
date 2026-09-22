namespace MCAROC_Analysis.Data.Entities;

/// <summary>Which external integration a health row tracks. Kept as a small fixed set (not free text) since
/// the breaker logic switches on it.</summary>
public enum IntegrationName
{
    ReferenceTool,
    LitigationApi,
    Vertex
}

/// <summary><see cref="Open"/> means the breaker has tripped and callers must not attempt the integration;
/// <see cref="Degraded"/> means failures are accumulating but the threshold hasn't been reached; <see
/// cref="Healthy"/> is the steady state. Only the health probe itself may transition <see cref="Open"/> back
/// to a working state (via the half-open single-probe claim) — a success reported by an ordinary in-flight
/// caller never does, per docs/pipeline-automation-plan.md §5.4.</summary>
public enum IntegrationHealthState
{
    Healthy,
    Degraded,
    Open
}

/// <summary>One row per <see cref="Name"/>, updated only via single-statement atomic SQL (never
/// read-modify-write — see docs/pipeline-automation-plan.md §5.4 for the exact race this must survive:
/// probes, workers, and the litigation client can all report a failure/success concurrently). <see
/// cref="LastTransitionUtc"/> fences stale results: a failure whose call started before this timestamp is
/// ignored, so a slow request that failed under old conditions can never re-open a breaker a probe just
/// closed, and a slow success can never re-close one a probe just opened.</summary>
public sealed class IntegrationHealth
{
    public long IntegrationHealthId { get; set; }
    public IntegrationName Name { get; set; }

    public IntegrationHealthState State { get; set; } = IntegrationHealthState.Healthy;
    public int ConsecutiveFailures { get; set; }
    public DateTime? LastSuccessUtc { get; set; }
    public string? LastError { get; set; }
    public DateTime? OpenedUtc { get; set; }
    public DateTime LastTransitionUtc { get; set; }
    /// <summary>Half-open claim target: the probe takes this with <c>UPDATE … SET NextProbeUtc=@now+@lease
    /// WHERE State='Open' AND NextProbeUtc &lt;= @now</c> — one winner, so concurrent probes never stampede a
    /// struggling integration.</summary>
    public DateTime? NextProbeUtc { get; set; }

    public byte[]? RowVersion { get; set; }
}
