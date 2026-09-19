namespace MCAROC_Analysis.Data.Entities;

/// <summary>
/// Structured, redacted internal activity and lifecycle audit event.
/// Contains zero-trust actor metadata, bounded payloads, and correlation IDs.
/// Personal data (such as raw client IP addresses) is strictly omitted.
/// </summary>
public class AuditLog
{
    public long AuditLogId { get; set; }
    public DateTime TimestampUtc { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");

    public ActorType ActorType { get; set; }
    public string ActorId { get; set; } = string.Empty;

    public AuditActionType Action { get; set; }
    public AuditEventKind EventKind { get; set; }

    public long? RequestId { get; set; }
    public string? EntityType { get; set; }
    public long? EntityId { get; set; }

    public AuditStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public string? EventPayloadJson { get; set; }
}
