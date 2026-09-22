using MCAROC_Analysis.Data;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.Audit;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.AnalystAccess;

/// <summary>Creates the single phase-one analyst through the local operator command only. This is not a
/// staff-management API: once any analyst exists, it fails closed.</summary>
public sealed class AnalystProvisioningService(
    AppDbContext db,
    IAnalystPasswordHasher passwordHasher,
    IAuditLogService auditLog)
{
    public async Task ProvisionInitialAsync(string loginName, string displayName, string password, CancellationToken ct = default)
    {
        var normalizedLogin = AnalystLoginNameNormalizer.Normalize(loginName);
        if (string.IsNullOrWhiteSpace(normalizedLogin) || string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A login name and display name are required.");
        if (displayName.Trim().Length > 100 || normalizedLogin.Length > 100)
            throw new ArgumentException("The login name or display name exceeds its allowed length.");
        if (password.Length < 16)
            throw new ArgumentException("The password must contain at least 16 characters.");
        if (await db.Analysts.AnyAsync(ct))
            throw new InvalidOperationException("An Analyst already exists. This phase-one command cannot create another account.");

        var analyst = new Analyst
        {
            LoginName = normalizedLogin,
            DisplayName = displayName.Trim(),
            PasswordHash = passwordHasher.Hash(password),
            CreatedUtc = DateTime.UtcNow
        };
        db.Analysts.Add(analyst);
        await db.SaveChangesAsync(ct);

        await auditLog.TryLogAsync(new AuditEvent(
            AuditActionType.AnalystProvisioned,
            AuditEventKind.DomainLifecycle,
            AuditStatus.Success,
            ActorType.SystemWorker,
            "AnalystProvisioningCommand",
            CorrelationContext.GenerateCorrelationId(),
            EntityType: nameof(Analyst),
            EntityId: analyst.AnalystId), ct);
    }
}
