using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.LitigationModule.Refresh;

/// <summary>A per-case lock that serialises the writers of a request's case columns: a vendor import, a live publication, and a change of the request's link to the case. It is a
/// SQL Server application lock owned by the current transaction, so it is released at commit or rollback and cannot leak. Everything that decides from "is this column portal-owned"
/// must take it first and read what it decides from <i>after</i> taking it; otherwise two writers can each act on the other's old state.</summary>
public static class CaseLock
{
    private const int TimeoutMs = 30_000;

    public static string Resource(long legacyCaseId) => $"litigation-case:{legacyCaseId}";

    public static string Resource(string key) => key.StartsWith("litigation-case", StringComparison.Ordinal) ? key : $"litigation-case:{key}";

    /// <summary>Takes the lock for the case. Requires an open transaction on <paramref name="db"/>; waits up to 30 s and throws if it cannot get it.</summary>
    public static Task AcquireAsync(AppDbContext db, long legacyCaseId, CancellationToken ct) =>
        AcquireResourceAsync(db, Resource(legacyCaseId), ct);

    /// <summary>Takes the lock for an arbitrary resource key. Requires an open transaction on <paramref name="db"/>; waits up to 30 s and throws if it cannot get it.</summary>
    public static Task AcquireAsync(AppDbContext db, string resourceKey, CancellationToken ct) =>
        AcquireResourceAsync(db, Resource(resourceKey), ct);

    private static async Task AcquireResourceAsync(AppDbContext db, string resourceName, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("A case lock is owned by a transaction; open one first.");
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; SELECT @r;";
        var resource = command.CreateParameter(); resource.ParameterName = "@resource"; resource.Value = resourceName; command.Parameters.Add(resource);
        var timeout = command.CreateParameter(); timeout.ParameterName = "@timeout"; timeout.Value = TimeoutMs; command.Parameters.Add(timeout);
        var code = Convert.ToInt32(await command.ExecuteScalarAsync(ct));
        if (code < 0) throw new TimeoutException($"Could not take the lock for resource '{resourceName}' (code {code}).");
    }
}
