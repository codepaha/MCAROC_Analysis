using MCAROC_Analysis.Data;
using Microsoft.EntityFrameworkCore;

namespace MCAROC_Analysis.Services.Pipeline;

/// <summary>A session-owned SQL Server <c>sp_getapplock</c> with a zero timeout, for fencing a sequence that
/// spans several transactions of its own (so a transaction-owned lock can't cover it) across every app
/// instance. <see cref="TryAcquireAsync"/> returns null when another caller holds it. The context's connection
/// stays open until the lock is disposed; a crashed process drops the lock along with its connection.</summary>
public sealed class SqlSessionLock : IAsyncDisposable
{
    private readonly AppDbContext _db;
    private readonly string _resource;

    private SqlSessionLock(AppDbContext db, string resource)
    {
        _db = db;
        _resource = resource;
    }

    public static async Task<SqlSessionLock?> TryAcquireAsync(AppDbContext db, string resource, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;";
            AddResource(cmd, resource);
            if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) >= 0)
                return new SqlSessionLock(db, resource);
        }
        catch
        {
            await db.Database.CloseConnectionAsync();
            throw;
        }
        await db.Database.CloseConnectionAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var cmd = _db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';";
            AddResource(cmd, _resource);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }

    private static void AddResource(System.Data.Common.DbCommand cmd, string resource)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = "@resource";
        p.Value = resource;
        cmd.Parameters.Add(p);
    }
}
