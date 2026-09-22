namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// The current request-scoped allocation. A unique RequestId makes one assignment authoritative; any
/// reassignment must be performed as a controlled mutation with its before/after state audit-recorded.
/// </summary>
public sealed class AnalystAssignment
{
    public long AnalystAssignmentId { get; set; }
    public long AnalystId { get; set; }
    public Analyst? Analyst { get; set; }
    public long RequestId { get; set; }
    public McaRequest? Request { get; set; }
    public DateTime AssignedUtc { get; set; }
    public string AssignedByActorId { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public byte[]? RowVersion { get; set; }
}
