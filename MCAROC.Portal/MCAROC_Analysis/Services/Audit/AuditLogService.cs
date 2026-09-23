using System;
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
    private int _consecutiveFailures;

    public int ConsecutiveFailures => _consecutiveFailures;

    public async Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class
    {
        AuditLog entry;
        try
        {
            entry = AuditLogEntryFactory.Create(evt);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to build audit entry for action {Action}", evt.Action);
            entry = AuditLogEntryFactory.Create(new AuditEvent<TruncatedPayloadEnvelope>(evt.Action, evt.EventKind,
                evt.Status, evt.ActorType, evt.ActorId, evt.CorrelationId, evt.RequestId, evt.EntityType,
                evt.EntityId, evt.ErrorMessage, new TruncatedPayloadEnvelope(true, typeof(T).Name)));
        }
        await PersistIsolatedAsync(entry, evt.Action, ct);
    }

    public async Task TryLogAsync(AuditEvent evt, CancellationToken ct = default)
    {
        await PersistIsolatedAsync(AuditLogEntryFactory.Create(evt), evt.Action, ct);
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

    public static string? SerializeAndCapPayload<T>(T? payload) where T : class =>
        AuditLogEntryFactory.SerializeAndCapPayload(payload);
}
