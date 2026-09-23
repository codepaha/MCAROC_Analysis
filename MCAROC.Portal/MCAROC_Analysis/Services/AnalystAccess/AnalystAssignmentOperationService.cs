using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AnalystAccess;

public sealed record AnalystAssignmentAuditPayload(
    long RequestId,
    long AnalystAssignmentId,
    long? PreviousAnalystId,
    long NewAnalystId,
    DateTime AssignedUtc,
    string? Reason);

public sealed record AnalystAssignmentOperationResult(
    long RequestId,
    long AnalystAssignmentId,
    long? PreviousAnalystId,
    long NewAnalystId,
    DateTime AssignedUtc,
    bool Changed);

/// <summary>Creates or reassigns the phase-one request allocation. Called by the local operator command;
/// it is deliberately not a web endpoint.</summary>
public sealed class AnalystAssignmentOperationService(AppDbContext db)
{
    public async Task<AnalystAssignmentOperationResult> AssignAsync(
        long requestId, string operatorId, string? reason, CancellationToken ct = default)
    {
        if (requestId <= 0)
            throw new ArgumentOutOfRangeException(nameof(requestId), "A positive request ID is required.");
        var actorId = operatorId?.Trim() ?? string.Empty;
        if (actorId.Length is 0 or > 200)
            throw new ArgumentException("A valid operator identifier is required (maximum 200 characters).", nameof(operatorId));
        var cleanReason = string.IsNullOrWhiteSpace(reason) ? null : AuditSanitizer.SanitizeAndCap(reason, 500);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Keep the read / insert / update sequence serialized across separate operator-command processes.
        await db.Database.ExecuteSqlRawAsync("SELECT TOP (1) 1 FROM [AnalystAssignments] WITH (TABLOCKX, HOLDLOCK)", ct);

        if (!await db.Requests.AnyAsync(request => request.RequestId == requestId, ct))
            throw new KeyNotFoundException("The requested work item does not exist.");

        var activeAnalysts = await db.Analysts.Where(analyst => analyst.IsActive)
            .Select(analyst => analyst.AnalystId).Take(2).ToListAsync(ct);
        if (activeAnalysts.Count != 1)
            throw new InvalidOperationException("Assignment requires exactly one active Analyst account.");
        var analystId = activeAnalysts[0];

        var assignment = await db.AnalystAssignments.SingleOrDefaultAsync(item => item.RequestId == requestId, ct);
        var previousAnalystId = assignment?.AnalystId;
        var assignedUtc = DateTime.UtcNow;
        if (assignment is not null && assignment.AnalystId == analystId && assignment.Reason == cleanReason)
        {
            await transaction.CommitAsync(ct);
            return new AnalystAssignmentOperationResult(requestId, assignment.AnalystAssignmentId,
                previousAnalystId, analystId, assignment.AssignedUtc, Changed: false);
        }

        if (assignment is null)
        {
            assignment = new AnalystAssignment
            {
                RequestId = requestId,
                AnalystId = analystId,
                AssignedUtc = assignedUtc,
                AssignedByActorId = actorId,
                Reason = cleanReason
            };
            db.AnalystAssignments.Add(assignment);
        }
        else
        {
            assignment.AnalystId = analystId;
            assignment.AssignedUtc = assignedUtc;
            assignment.AssignedByActorId = actorId;
            assignment.Reason = cleanReason;
        }

        // Materialize a new assignment's identity before including it in the audit event. This flush is
        // still inside the explicit transaction, so a later audit-insert failure rolls it back.
        await db.SaveChangesAsync(ct);

        var auditEvent = new AuditEvent<AnalystAssignmentAuditPayload>(
            AuditActionType.AnalystAssignmentChanged,
            AuditEventKind.DomainLifecycle,
            AuditStatus.Success,
            ActorType.UnverifiedOperator,
            actorId,
            CorrelationContext.GenerateCorrelationId(),
            RequestId: requestId,
            EntityType: nameof(AnalystAssignment),
            EntityId: assignment.AnalystAssignmentId,
            Payload: new AnalystAssignmentAuditPayload(requestId, assignment.AnalystAssignmentId,
                previousAnalystId, analystId, assignedUtc, cleanReason));
        db.AuditLogs.Add(AuditLogEntryFactory.Create(auditEvent));

        // Assignment and required audit evidence must commit or roll back together. The generic audit
        // logger is intentionally best-effort and writes through a separate context, so it cannot be
        // used for this operation's acceptance boundary.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        return new AnalystAssignmentOperationResult(requestId, assignment.AnalystAssignmentId,
            previousAnalystId, analystId, assignedUtc, Changed: true);
    }
}
