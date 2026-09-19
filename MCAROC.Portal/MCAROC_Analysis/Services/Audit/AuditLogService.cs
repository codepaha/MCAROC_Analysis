using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Audit;

public class AuditLogService(IServiceScopeFactory scopeFactory, ILogger<AuditLogService> logger) : IAuditLogService
{
    public static readonly EventId AuditWriteFailureEventId = new(9001, "MCAROC_AUDIT_WRITE_FAILURE");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private int _consecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    public async Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class
    {
        string? payloadJson = null;
        try
        {
            payloadJson = SerializeAndCapPayload(evt.Payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to serialize audit payload for action {Action}", evt.Action);
            payloadJson = JsonSerializer.Serialize(new TruncatedPayloadEnvelope(true, typeof(T).Name), JsonOptions);
        }

        var entry = new AuditLog
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

        await PersistIsolatedAsync(entry, evt.Action, ct);
    }

    public async Task TryLogAsync(AuditEvent evt, CancellationToken ct = default)
    {
        var entry = new AuditLog
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

        await PersistIsolatedAsync(entry, evt.Action, ct);
    }

    private async Task PersistIsolatedAsync(AuditLog entry, AuditActionType action, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await db.AuditLogs.AddAsync(entry, ct);
            await db.SaveChangesAsync(ct);

            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception ex)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            if (failures == 1 || failures % 10 == 0)
            {
                logger.LogCritical(AuditWriteFailureEventId, ex,
                    "Failed to persist audit log entry {Action} (Consecutive failures: {Count})", action, failures);
            }
        }
    }

    public static string? SerializeAndCapPayload<T>(T? payload) where T : class
    {
        if (payload is null) return null;
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        if (json.Length <= 2000) return json;

        // Valid compact JSON envelope fallback for oversized payloads
        return JsonSerializer.Serialize(new TruncatedPayloadEnvelope(true, typeof(T).Name), JsonOptions);
    }

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
