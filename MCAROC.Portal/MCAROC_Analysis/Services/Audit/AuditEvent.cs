using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Audit;

public record AuditEvent<T>(
    AuditActionType Action,
    AuditEventKind EventKind,
    AuditStatus Status,
    ActorType ActorType,
    string ActorId,
    string CorrelationId,
    long? RequestId = null,
    string? EntityType = null,
    long? EntityId = null,
    string? ErrorMessage = null,
    T? Payload = null
) where T : class;

public record AuditEvent(
    AuditActionType Action,
    AuditEventKind EventKind,
    AuditStatus Status,
    ActorType ActorType,
    string ActorId,
    string CorrelationId,
    long? RequestId = null,
    string? EntityType = null,
    long? EntityId = null,
    string? ErrorMessage = null
);
