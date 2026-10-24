using System.Text.Json;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Audit;

/// <summary>Builds the canonical persisted representation of an audit event.</summary>
public static class AuditLogEntryFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static AuditLog Create<T>(AuditEvent<T> evt) where T : class
    {
        var payloadJson = SerializeAndCapPayload(evt.Payload);

        return new AuditLog
        {
            TimestampUtc = DateTime.UtcNow,
            CorrelationId = BoundString(evt.CorrelationId, 64),
            ActorType = evt.ActorType,
            ActorId = BoundString(evt.ActorId, 200),
            Action = evt.Action,
            EventKind = evt.EventKind,
            RequestId = evt.RequestId,
            EntityType = BoundNullableString(evt.EntityType, 80),
            EntityId = evt.EntityId,
            Status = evt.Status,
            ErrorMessage = string.IsNullOrEmpty(evt.ErrorMessage) ? null : AuditSanitizer.SanitizeAndCap(evt.ErrorMessage, 500),
            EventPayloadJson = payloadJson
        };
    }

    public static AuditLog Create(AuditEvent evt) => new()
    {
        TimestampUtc = DateTime.UtcNow,
        CorrelationId = BoundString(evt.CorrelationId, 64),
        ActorType = evt.ActorType,
        ActorId = BoundString(evt.ActorId, 200),
        Action = evt.Action,
        EventKind = evt.EventKind,
        RequestId = evt.RequestId,
        EntityType = BoundNullableString(evt.EntityType, 80),
        EntityId = evt.EntityId,
        Status = evt.Status,
        ErrorMessage = string.IsNullOrEmpty(evt.ErrorMessage) ? null : AuditSanitizer.SanitizeAndCap(evt.ErrorMessage, 500),
        EventPayloadJson = null
    };

    /// <summary>The longest payload kept as written; a longer one is replaced by a <see cref="TruncatedPayloadEnvelope"/>.</summary>
    public const int MaxPayloadLength = 2000;

    public static string? SerializeAndCapPayload<T>(T? payload) where T : class
    {
        if (payload is null) return null;
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return json.Length <= MaxPayloadLength
            ? json
            : JsonSerializer.Serialize(new TruncatedPayloadEnvelope(true, typeof(T).Name), JsonOptions);
    }

    /// <summary>Whether <paramref name="payload"/> is kept whole, measured as it is serialized (escaped characters count at their escaped length).</summary>
    public static bool FitsPayloadCap<T>(T payload) where T : class => JsonSerializer.Serialize(payload, JsonOptions).Length <= MaxPayloadLength;

    private static string BoundString(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }

    private static string? BoundNullableString(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen];
    }
}
