using MCAROC_Analysis.Migrations;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves the enforced pre-flight guard in migration <c>MultiHolderOperationalSlotLeases</c>
/// actually fires and actually closes the check-then-drop race — not just that the migration file
/// contains the right-looking SQL text. Runs <see cref="OperationalSlotLeaseMigrationGuard.Sql"/> verbatim
/// (the exact text the migration executes) against a real, dedicated, throwaway SQL Server database
/// rather than the shared <c>MCAROC_Analysis_Test</c> — this only needs a bare table shaped like
/// <c>OperationalSlotLeases</c>, not the whole app schema, and a dedicated database avoids any interaction
/// with other tests' or other branches' migration state on the shared instance.
///
/// Every connection string is derived from <see cref="TestDatabase.ConnectionString"/> (which already
/// respects the <c>MCAROC_TEST_CONNECTION</c> override the hosted CI job sets) rather than a hardcoded
/// local instance — the hosted <c>build-and-test</c> job points that override at a SQL-auth container on
/// <c>localhost,1433</c>, not a named Windows instance.</summary>
public class OperationalSlotLeaseMigrationGuardTests : IAsyncLifetime
{
    private readonly string _databaseName = "MCAROC_MigrationGuard_" + Guid.NewGuid().ToString("N")[..12];

    private static string ConnectionStringFor(string database)
    {
        var builder = new SqlConnectionStringBuilder(TestDatabase.ConnectionString) { InitialCatalog = database };
        return builder.ConnectionString;
    }

    private string MasterConnectionString => ConnectionStringFor("master");
    private string DatabaseConnectionString => ConnectionStringFor(_databaseName);

    public async Task InitializeAsync()
    {
        await using var master = new SqlConnection(MasterConnectionString);
        await master.OpenAsync();
        await using var create = master.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{_databaseName}]";
        await create.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            await using var master = new SqlConnection(MasterConnectionString);
            await master.OpenAsync();
            await using var drop = master.CreateCommand();
            drop.CommandText = $"ALTER DATABASE [{_databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_databaseName}];";
            await drop.ExecuteNonQueryAsync();
        }
        catch { /* best effort */ }
    }

    /// <summary>Mirrors the OLD (pre-migration) shape closely enough for the guard, which only reads
    /// ExpiresUtc — the exact column set either side of the migration doesn't matter to it.</summary>
    private static async Task CreateLeaseTableAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE OperationalSlotLeases (SlotType nvarchar(30) NOT NULL PRIMARY KEY, ActiveHolderId nvarchar(100) NULL, ExpiresUtc datetime2 NULL);";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertLeaseAsync(SqlConnection conn, string slotType, DateTime? expiresUtc)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO OperationalSlotLeases (SlotType, ActiveHolderId, ExpiresUtc) VALUES (@slot, @holder, @expires);";
        cmd.Parameters.AddWithValue("@slot", slotType);
        cmd.Parameters.AddWithValue("@holder", "test-holder");
        cmd.Parameters.AddWithValue("@expires", (object?)expiresUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the guard's exact SQL inside its own explicit transaction — required now that the
    /// guard itself takes sp_getapplock with @LockOwner = 'Transaction', which needs an active transaction
    /// to attach to, exactly mirroring how EF wraps a real migration's Up()/Down() in one transaction.
    /// Commits on success (nothing to roll back — the guard makes no data changes of its own) so the
    /// locks release immediately rather than lingering until the connection closes.</summary>
    private async Task<SqlException?> RunGuardAsync()
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = OperationalSlotLeaseMigrationGuard.Sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
            await tx.CommitAsync();
            return null;
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync();
            return ex;
        }
    }

    [Fact]
    public async Task Guard_is_a_no_op_when_the_table_does_not_exist_yet()
    {
        // A fresh database (this migration's own Down(), or a brand-new deploy) — nothing to quiesce.
        var ex = await RunGuardAsync();
        Assert.Null(ex);
    }

    [Fact]
    public async Task Guard_is_a_no_op_when_no_lease_is_active()
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        await CreateLeaseTableAsync(conn);
        await InsertLeaseAsync(conn, "LargeUnpack", expiresUtc: null); // released
        await InsertLeaseAsync(conn, "LargeUpload", expiresUtc: DateTime.UtcNow.AddMinutes(-5)); // expired

        var ex = await RunGuardAsync();

        Assert.Null(ex);
    }

    [Fact]
    public async Task Guard_throws_when_a_lease_is_still_active()
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        await CreateLeaseTableAsync(conn);
        await InsertLeaseAsync(conn, "LargeUnpack", expiresUtc: DateTime.UtcNow.AddMinutes(5)); // still active

        var ex = await RunGuardAsync();

        Assert.NotNull(ex);
        Assert.Contains("still active", ex!.Message);
        Assert.Contains("Stop or drain FilingProcessingWorker", ex.Message);
    }

    [Fact]
    public async Task Guard_throws_if_even_one_of_several_leases_is_active()
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        await CreateLeaseTableAsync(conn);
        await InsertLeaseAsync(conn, "LargeUpload", expiresUtc: DateTime.UtcNow.AddMinutes(-5)); // expired — fine alone
        await InsertLeaseAsync(conn, "LargeUnpack", expiresUtc: DateTime.UtcNow.AddHours(1)); // still active

        var ex = await RunGuardAsync();

        Assert.NotNull(ex);
    }

    /// <summary>The actual review finding, proved directly: the guard must hold the SAME sp_getapplock
    /// resources a real TryAcquireSlotAsync call takes, for its WHOLE transaction — not just for the
    /// instant of the existence check — so nothing can acquire a brand-new lease in the gap between the
    /// guard's check and the migration's later DropTable. A plain, lock-free "IF EXISTS" (the first version
    /// of this guard) could pass with zero active leases and then let a real acquisition slip in and commit
    /// before DropTable ran, which would then destroy that brand-new lease anyway.
    ///
    /// Tested as a direct mutual-exclusion proof rather than a timing race: while the guard's transaction
    /// is still open (even against an empty/nonexistent table, where the existence check itself has nothing
    /// to find), a concurrent attempt to take the SAME sp_getapplock resource a real acquisition would take
    /// must be refused (short LockTimeout, so the test doesn't wait out the guard's real 30s timeout) —
    /// proving the lock is held for the guard's whole duration, not released the instant the check
    /// finishes. Once the guard's transaction ends, the identical acquisition attempt must succeed.</summary>
    [Fact]
    public async Task Guard_holds_the_slot_locks_for_its_whole_transaction_blocking_a_concurrent_acquisition()
    {
        const string resource = "OperationalSlot_LargeUnpack";

        await using var guardConn = new SqlConnection(DatabaseConnectionString);
        await guardConn.OpenAsync();
        var guardTx = (SqlTransaction)await guardConn.BeginTransactionAsync();
        await using (var guardCmd = guardConn.CreateCommand())
        {
            guardCmd.Transaction = guardTx;
            guardCmd.CommandText = OperationalSlotLeaseMigrationGuard.Sql;
            await guardCmd.ExecuteNonQueryAsync(); // table doesn't exist yet — no throw, but the locks stay held in guardTx
        }

        // A concurrent attempt on the SAME resource, shaped exactly like
        // OperationalSlotLeaseService.TryAcquireSlotAsync's own sp_getapplock call, with a short timeout.
        var blockedResult = await TryLockAsync(resource, lockTimeoutMs: 500);
        Assert.True(blockedResult < 0,
            $"Expected the concurrent acquisition to be refused while the guard's transaction is open, but sp_getapplock returned {blockedResult} (>= 0 means it succeeded).");

        await guardTx.RollbackAsync(); // the migration itself would COMMIT; rollback is equivalent for "the transaction ended" here

        // Once the guard's transaction has ended, the identical acquisition attempt succeeds immediately.
        var freedResult = await TryLockAsync(resource, lockTimeoutMs: 5000);
        Assert.True(freedResult >= 0, $"Expected the lock to be available once the guard's transaction ended, but sp_getapplock returned {freedResult}.");
    }

    private async Task<int> TryLockAsync(string resource, int lockTimeoutMs)
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        var tx = (SqlTransaction)await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; SELECT @r;";
            cmd.Parameters.AddWithValue("@resource", resource);
            cmd.Parameters.AddWithValue("@timeout", lockTimeoutMs);
            var result = await cmd.ExecuteScalarAsync();
            return (int)result!;
        }
        finally
        {
            await tx.RollbackAsync();
        }
    }
}
