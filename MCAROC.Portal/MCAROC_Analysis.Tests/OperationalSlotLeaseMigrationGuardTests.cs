using MCAROC_Analysis.Migrations;
using Microsoft.Data.SqlClient;

namespace MCAROC_Analysis.Tests;

/// <summary>Proves the enforced pre-flight guard in migration <c>MultiHolderOperationalSlotLeases</c>
/// actually fires — not just that the migration file contains the right-looking SQL text. Runs
/// <see cref="OperationalSlotLeaseMigrationGuard.Sql"/> verbatim (the exact text the migration executes)
/// against a real, dedicated, throwaway SQL Server database rather than the shared
/// <c>MCAROC_Analysis_Test</c> — this only needs a bare table shaped like <c>OperationalSlotLeases</c>,
/// not the whole app schema, and a dedicated database avoids any interaction with other tests' or other
/// branches' migration state on the shared instance.</summary>
public class OperationalSlotLeaseMigrationGuardTests : IAsyncLifetime
{
    private readonly string _databaseName = "MCAROC_MigrationGuard_" + Guid.NewGuid().ToString("N")[..12];

    // Built from TestDatabase.ConnectionString (which already respects the MCAROC_TEST_CONNECTION
    // override) rather than a hardcoded .\SQLEXPRESS/Trusted_Connection string — the hosted CI runner's
    // build-and-test job points that override at a SQL-auth container on localhost,1433, not a named
    // local instance, and a hardcoded local connection string fails outright there (found live: CI red,
    // "server not found", after this file first shipped with exactly that hardcoded string).
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
    private async Task CreateLeaseTableAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE OperationalSlotLeases (SlotType nvarchar(30) NOT NULL PRIMARY KEY, ActiveHolderId nvarchar(100) NULL, ExpiresUtc datetime2 NULL);";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertLeaseAsync(SqlConnection conn, string slotType, DateTime? expiresUtc)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO OperationalSlotLeases (SlotType, ActiveHolderId, ExpiresUtc) VALUES (@slot, @holder, @expires);";
        cmd.Parameters.AddWithValue("@slot", slotType);
        cmd.Parameters.AddWithValue("@holder", "test-holder");
        cmd.Parameters.AddWithValue("@expires", (object?)expiresUtc ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<SqlException?> RunGuardAsync()
    {
        await using var conn = new SqlConnection(DatabaseConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = OperationalSlotLeaseMigrationGuard.Sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (SqlException ex)
        {
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
}
