using System.Threading;
using System.Threading.Tasks;

namespace MCAROC_Analysis.Services.Audit;

public interface IAuditLogService
{
    Task TryLogAsync<T>(AuditEvent<T> evt, CancellationToken ct = default) where T : class;
    Task TryLogAsync(AuditEvent evt, CancellationToken ct = default);
}
